﻿using System;
using System.Collections.Generic;
using System.Linq;

using Kiota.Builder.CodeDOM;
using Kiota.Builder.Extensions;

namespace Kiota.Builder.Writers.Python;
public class CodeMethodWriter : BaseElementWriter<CodeMethod, PythonConventionService>
{
    private readonly CodeUsingWriter _codeUsingWriter;
    public CodeMethodWriter(PythonConventionService conventionService, string clientNamespaceName, bool usesBackingStore) : base(conventionService)
    {
        _codeUsingWriter = new(clientNamespaceName);
        _usesBackingStore = usesBackingStore;
    }
    private readonly bool _usesBackingStore;
    public override void WriteCodeElement(CodeMethod codeElement, LanguageWriter writer)
    {
        ArgumentNullException.ThrowIfNull(codeElement);
        if (codeElement.ReturnType == null) throw new InvalidOperationException($"{nameof(codeElement.ReturnType)} should not be null");
        ArgumentNullException.ThrowIfNull(writer);
        if (codeElement.Parent is not CodeClass parentClass) throw new InvalidOperationException("the parent of a method should be a class");

        var returnType = conventions.GetTypeString(codeElement.ReturnType, codeElement, true, writer);
        var isVoid = "None".Equals(returnType, StringComparison.OrdinalIgnoreCase);
        if (parentClass.IsOfKind(CodeClassKind.Model) && (codeElement.IsOfKind(CodeMethodKind.Setter) || codeElement.IsOfKind(CodeMethodKind.Getter) || codeElement.IsOfKind(CodeMethodKind.Constructor)))
        {
            writer.IncreaseIndent();
        }
        else
        {
            WriteMethodPrototype(codeElement, writer, returnType, isVoid);
            writer.IncreaseIndent();
            WriteMethodDocumentation(codeElement, writer, returnType, isVoid);
        }
        var inherits = parentClass.StartBlock.Inherits != null && !parentClass.IsErrorDefinition;
        var requestBodyParam = codeElement.Parameters.OfKind(CodeParameterKind.RequestBody);
        var requestConfigParam = codeElement.Parameters.OfKind(CodeParameterKind.RequestConfiguration);
        var requestParams = new RequestParams(requestBodyParam, requestConfigParam);
        if (!codeElement.IsOfKind(CodeMethodKind.Setter) &&
        !(codeElement.IsOfKind(CodeMethodKind.Constructor) && parentClass.IsOfKind(CodeClassKind.RequestBuilder)))
            foreach (var parameter in codeElement.Parameters.Where(static x => !x.Optional).OrderBy(static x => x.Name))
            {
                var parameterName = parameter.Name.ToSnakeCase();
                writer.StartBlock($"if not {parameterName}:");
                writer.WriteLine($"raise TypeError(\"{parameterName} cannot be null.\")");
                writer.DecreaseIndent();
            }
        switch (codeElement.Kind)
        {
            case CodeMethodKind.ClientConstructor:
                WriteConstructorBody(parentClass, codeElement, writer, inherits);
                WriteApiConstructorBody(parentClass, codeElement, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.Constructor:
                WriteConstructorBody(parentClass, codeElement, writer, inherits);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.IndexerBackwardCompatibility:
                WriteIndexerBody(codeElement, parentClass, returnType, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.Deserializer:
                WriteDeserializerBody(codeElement, parentClass, writer, inherits);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.Serializer:
                WriteSerializerBody(inherits, parentClass, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.RequestGenerator:
                WriteRequestGeneratorBody(codeElement, requestParams, parentClass, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.RequestExecutor:
                WriteRequestExecutorBody(codeElement, requestParams, parentClass, isVoid, returnType, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.Getter:
                WriteGetterBody(codeElement, writer, parentClass);
                break;
            case CodeMethodKind.Setter:
                WriteSetterBody(codeElement, writer, parentClass);
                break;
            case CodeMethodKind.RequestBuilderWithParameters:
                WriteRequestBuilderWithParametersBody(codeElement, parentClass, returnType, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.QueryParametersMapper:
                WriteQueryParametersMapper(codeElement, parentClass, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.Factory:
                WriteFactoryMethodBody(codeElement, parentClass, writer);
                writer.CloseBlock(string.Empty);
                break;
            case CodeMethodKind.RawUrlConstructor:
                throw new InvalidOperationException("RawUrlConstructor is not supported in python");
            case CodeMethodKind.RequestBuilderBackwardCompatibility:
                throw new InvalidOperationException("RequestBuilderBackwardCompatibility is not supported as the request builders are implemented by properties.");
            default:
                WriteDefaultMethodBody(codeElement, writer, returnType);
                writer.CloseBlock(string.Empty);
                break;
        }
    }
    private const string DiscriminatorMappingVarName = "mapping_value";

    private static readonly CodePropertyTypeComparer CodePropertyTypeForwardComparer = new();
    private static readonly CodePropertyTypeComparer CodePropertyTypeBackwardComparer = new(true);
    private void WriteFactoryMethodBodyForInheritedModel(CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var mappedType in parentClass.DiscriminatorInformation.DiscriminatorMappings.OrderBy(static x => x.Key))
        {
            writer.StartBlock($"if {DiscriminatorMappingVarName} and {DiscriminatorMappingVarName}.casefold() == \"{mappedType.Key}\".casefold():");
            var mappedTypeName = mappedType.Value.AllTypes.First().Name;
            _codeUsingWriter.WriteDeferredImport(parentClass, mappedTypeName, writer);
            writer.WriteLine($"return {mappedTypeName.ToFirstCharacterUpperCase()}()");
            writer.DecreaseIndent();
        }
        writer.WriteLine($"return {parentClass.Name.ToFirstCharacterUpperCase()}()");
    }
    private const string ResultVarName = "result";
    private void WriteFactoryMethodBodyForUnionModel(CodeMethod codeElement, CodeClass parentClass, CodeParameter parseNodeParameter, LanguageWriter writer)
    {
        writer.WriteLine($"{ResultVarName} = {parentClass.Name.ToFirstCharacterUpperCase()}()");
        var includeElse = false;
        foreach (var property in parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                            .ThenBy(static x => x.Name))
        {
            if (property.Type is CodeType propertyType)
                if (propertyType.TypeDefinition is CodeClass && !propertyType.IsCollection)
                {
                    var mappedType = parentClass.DiscriminatorInformation.DiscriminatorMappings.FirstOrDefault(x => x.Value.Name.Equals(propertyType.Name, StringComparison.OrdinalIgnoreCase));
                    writer.StartBlock($"{(includeElse ? "el" : string.Empty)}if {DiscriminatorMappingVarName} and {DiscriminatorMappingVarName}.casefold() == \"{mappedType.Key}\".casefold():");
                    _codeUsingWriter.WriteDeferredImport(parentClass, propertyType.Name, writer);
                    writer.WriteLine($"{ResultVarName}.{property.Name.ToSnakeCase()} = {propertyType.Name.ToFirstCharacterUpperCase()}()");
                    writer.DecreaseIndent();
                }
                else if (propertyType.TypeDefinition is CodeClass && propertyType.IsCollection || propertyType.TypeDefinition is null || propertyType.TypeDefinition is CodeEnum)
                {
                    var valueVarName = $"{property.Name.ToSnakeCase()}_value";
                    writer.StartBlock($"{(includeElse ? "el" : string.Empty)}if {valueVarName} := {parseNodeParameter.Name.ToSnakeCase()}.{GetDeserializationMethodName(propertyType, codeElement, parentClass)}:");
                    writer.WriteLine($"{ResultVarName}.{property.Name.ToSnakeCase()} = {valueVarName}");
                    writer.DecreaseIndent();
                }
            if (!includeElse)
                includeElse = true;
        }
        writer.WriteLine($"return {ResultVarName}");
    }
    private void WriteFactoryMethodBodyForIntersectionModel(CodeMethod codeElement, CodeClass parentClass, CodeParameter parseNodeParameter, LanguageWriter writer)
    {
        writer.WriteLine($"{ResultVarName} = {parentClass.Name.ToFirstCharacterUpperCase()}()");
        var includeElse = false;
        foreach (var property in parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is not CodeType propertyType || propertyType.IsCollection || propertyType.TypeDefinition is not CodeClass)
                                            .OrderBy(static x => x, CodePropertyTypeBackwardComparer)
                                            .ThenBy(static x => x.Name))
        {
            if (property.Type is CodeType propertyType)
            {
                var valueVarName = $"{property.Name.ToSnakeCase()}_value";
                writer.StartBlock($"{(includeElse ? "el" : string.Empty)}if {valueVarName} := {parseNodeParameter.Name.ToSnakeCase()}.{GetDeserializationMethodName(propertyType, codeElement, parentClass)}:");
                writer.WriteLine($"{ResultVarName}.{property.Name.ToSnakeCase()} = {valueVarName}");
                writer.DecreaseIndent();
            }
            if (!includeElse)
                includeElse = true;
        }
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType xType && xType.TypeDefinition is CodeClass && !xType.IsCollection)
                                            .Select(static x => new Tuple<CodeProperty, CodeType>(x, (CodeType)x.Type))
                                            .ToArray();
        if (complexProperties.Any())
        {
            if (includeElse)
            {
                writer.StartBlock("else:");
            }
            foreach (var property in complexProperties)
            {
                _codeUsingWriter.WriteDeferredImport(parentClass, property.Item2.Name, writer);
                writer.WriteLine($"{ResultVarName}.{property.Item1.Name.ToSnakeCase()} = {property.Item2.Name.ToFirstCharacterUpperCase()}()");
            }
            if (includeElse)
            {
                writer.DecreaseIndent();
            }
        }
        writer.WriteLine($"return {ResultVarName}");
    }
    private void WriteFactoryMethodBody(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        var parseNodeParameter = codeElement.Parameters.OfKind(CodeParameterKind.ParseNode) ?? throw new InvalidOperationException("Factory method should have a ParseNode parameter");

        if (parentClass.DiscriminatorInformation.ShouldWriteParseNodeCheck && !parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
        {
            writer.StartBlock("try:");
            writer.WriteLine($"{DiscriminatorMappingVarName} = {parseNodeParameter.Name.ToSnakeCase()}.get_child_node(\"{parentClass.DiscriminatorInformation.DiscriminatorPropertyName}\").get_str_value()");
            writer.DecreaseIndent();
            writer.StartBlock($"except AttributeError:");
            writer.WriteLine($"{DiscriminatorMappingVarName} = None");
            writer.DecreaseIndent();
        }
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForInheritedType)
            WriteFactoryMethodBodyForInheritedModel(parentClass, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
            WriteFactoryMethodBodyForUnionModel(codeElement, parentClass, parseNodeParameter, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteFactoryMethodBodyForIntersectionModel(codeElement, parentClass, parseNodeParameter, writer);
        else
            writer.WriteLine($"return {parentClass.Name.ToFirstCharacterUpperCase()}()");
    }
    private void WriteIndexerBody(CodeMethod codeElement, CodeClass parentClass, string returnType, LanguageWriter writer)
    {
        _codeUsingWriter.WriteDeferredImport(parentClass, codeElement.ReturnType.Name, writer);
        if (parentClass.GetPropertyOfKind(CodePropertyKind.PathParameters) is CodeProperty pathParametersProperty &&
            codeElement.OriginalIndexer != null)
            conventions.AddParametersAssignment(writer, pathParametersProperty.Type, $"self.{pathParametersProperty.Name}",
                (codeElement.OriginalIndexer.IndexType, codeElement.OriginalIndexer.SerializationName, codeElement.OriginalIndexer.IndexParameterName.ToSnakeCase()));
        conventions.AddRequestBuilderBody(parentClass, returnType, writer, conventions.TempDictionaryVarName);
    }
    private void WriteRequestBuilderWithParametersBody(CodeMethod codeElement, CodeClass parentClass, string returnType, LanguageWriter writer)
    {
        _codeUsingWriter.WriteDeferredImport(parentClass, codeElement.ReturnType.Name, writer);
        var codePathParameters = codeElement.Parameters
                                                    .Where(x => x.IsOfKind(CodeParameterKind.Path));
        conventions.AddRequestBuilderBody(parentClass, returnType, writer, pathParameters: codePathParameters);
    }
    private static void WriteApiConstructorBody(CodeClass parentClass, CodeMethod method, LanguageWriter writer)
    {
        if (parentClass.GetPropertyOfKind(CodePropertyKind.RequestAdapter) is not CodeProperty requestAdapterProperty) return;
        var backingStoreParameter = method.Parameters.OfKind(CodeParameterKind.BackingStore);
        var requestAdapterPropertyName = requestAdapterProperty.Name.ToSnakeCase();
        var pathParametersProperty = parentClass.GetPropertyOfKind(CodePropertyKind.PathParameters);
        WriteSerializationRegistration(method.SerializerModules, writer, "register_default_serializer");
        WriteSerializationRegistration(method.DeserializerModules, writer, "register_default_deserializer");
        if (!string.IsNullOrEmpty(method.BaseUrl))
        {
            writer.StartBlock($"if not self.{requestAdapterPropertyName}.base_url:");
            writer.WriteLine($"self.{requestAdapterPropertyName}.base_url = \"{method.BaseUrl}\"");
            writer.DecreaseIndent();
            if (pathParametersProperty != null)
                writer.WriteLine($"self.{pathParametersProperty.Name.ToSnakeCase()}[\"base_url\"] = self.{requestAdapterPropertyName}.base_url");
        }
        if (backingStoreParameter != null)
            writer.WriteLine($"self.{requestAdapterPropertyName}.enable_backing_store({backingStoreParameter.Name})");
    }
    private static void WriteQueryParametersMapper(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        var parameter = codeElement.Parameters.FirstOrDefault(x => x.IsOfKind(CodeParameterKind.QueryParametersMapperParameter));
        if (parameter == null) throw new InvalidOperationException("QueryParametersMapper should have a parameter of type QueryParametersMapper");
        var parameterName = parameter.Name.ToSnakeCase();
        var escapedProperties = parentClass.Properties.Where(x => x.IsOfKind(CodePropertyKind.QueryParameter) && x.IsNameEscaped);
        var unescapedProperties = parentClass.Properties.Where(x => x.IsOfKind(CodePropertyKind.QueryParameter) && !x.IsNameEscaped);
        foreach (var escapedProperty in escapedProperties)
        {
            writer.StartBlock($"if {parameterName} == \"{escapedProperty.Name.ToSnakeCase()}\":");
            writer.WriteLine($"return \"{escapedProperty.SerializationName}\"");
            writer.DecreaseIndent();
        }
        foreach (var unescapedProperty in unescapedProperties.Select(x => x.Name))
        {
            writer.StartBlock($"if {parameterName} == \"{unescapedProperty.ToSnakeCase()}\":");
            writer.WriteLine($"return \"{unescapedProperty}\"");
            writer.DecreaseIndent();
        }
        writer.WriteLine($"return {parameterName}");
    }
    private static void WriteSerializationRegistration(HashSet<string> serializationModules, LanguageWriter writer, string methodName)
    {
        if (serializationModules != null)
            foreach (var module in serializationModules)
                writer.WriteLine($"{methodName}({module})");
    }
    private CodePropertyKind[]? _DirectAccessProperties;
    private CodePropertyKind[] DirectAccessProperties
    {
        get
        {
            if (_DirectAccessProperties == null)
            {
                var directAccessProperties = new List<CodePropertyKind> {
                CodePropertyKind.BackingStore,
                CodePropertyKind.RequestBuilder,
                CodePropertyKind.UrlTemplate,
                CodePropertyKind.PathParameters
            };
                if (!_usesBackingStore)
                {
                    directAccessProperties.Add(CodePropertyKind.AdditionalData);
                }
                _DirectAccessProperties = directAccessProperties.ToArray();
            }
            return _DirectAccessProperties;
        }
    }
    private CodePropertyKind[]? _SetterAccessProperties;
    private CodePropertyKind[] SetterAccessProperties
    {
        get
        {
            _SetterAccessProperties ??= new CodePropertyKind[] {
                    CodePropertyKind.AdditionalData, //additional data and custom properties need to use the accessors in case of backing store use
                    CodePropertyKind.Custom
                }.Except(DirectAccessProperties)
                .ToArray();
            return _SetterAccessProperties;
        }
    }
    private void WriteConstructorBody(CodeClass parentClass, CodeMethod currentMethod, LanguageWriter writer, bool inherits)
    {
        if (inherits && !parentClass.IsOfKind(CodeClassKind.Model))
        {
            if (parentClass.IsOfKind(CodeClassKind.RequestBuilder) &&
            currentMethod.Parameters.OfKind(CodeParameterKind.RequestAdapter) is CodeParameter requestAdapterParameter &&
            parentClass.Properties.FirstOrDefaultOfKind(CodePropertyKind.UrlTemplate) is CodeProperty urlTemplateProperty)
            {
                if (currentMethod.Parameters.OfKind(CodeParameterKind.PathParameters) is CodeParameter pathParametersParameter)
                    writer.WriteLine($"super().__init__({requestAdapterParameter.Name.ToSnakeCase()}, {urlTemplateProperty.DefaultValue ?? ""}, {pathParametersParameter.Name.ToSnakeCase()})");
                else
                    writer.WriteLine($"super().__init__({requestAdapterParameter.Name.ToSnakeCase()}, {urlTemplateProperty.DefaultValue ?? ""}, None)");
            }
            else
                writer.WriteLine("super().__init__()");
        }
        if (parentClass.IsOfKind(CodeClassKind.Model))
        {
            writer.DecreaseIndent();
        }

        if (!(parentClass.IsOfKind(CodeClassKind.RequestBuilder) && currentMethod.IsOfKind(CodeMethodKind.Constructor, CodeMethodKind.ClientConstructor)))
        {
            WriteDirectAccessProperties(parentClass, writer);
            WriteSetterAccessProperties(parentClass, writer);
            WriteSetterAccessPropertiesWithoutDefaults(parentClass, writer);
            if (currentMethod.Parameters.OfKind(CodeParameterKind.PathParameters) is CodeParameter pathParametersParam)
                conventions.AddParametersAssignment(writer,
                                                pathParametersParam.Type.AllTypes.OfType<CodeType>().FirstOrDefault(),
                                                pathParametersParam.Name.ToFirstCharacterLowerCase(),
                                                currentMethod.Parameters
                                                            .Where(x => x.IsOfKind(CodeParameterKind.Path))
                                                            .Select(x => (x.Type, x.SerializationName, x.Name.ToFirstCharacterLowerCase()))
                                                            .ToArray());
            AssignPropertyFromParameter(parentClass, currentMethod, CodeParameterKind.PathParameters, CodePropertyKind.PathParameters, writer, conventions.TempDictionaryVarName);
        }

        if (parentClass.IsOfKind(CodeClassKind.Model))
        {
            writer.IncreaseIndent();
        }
    }
    private void WriteDirectAccessProperties(CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var propWithDefault in parentClass.GetPropertiesOfKind(DirectAccessProperties)
                                        .Where(static x => !string.IsNullOrEmpty(x.DefaultValue))
                                        .OrderByDescending(static x => x.Kind)
                                        .ThenBy(static x => x.Name))
        {
            var returnType = conventions.GetTypeString(propWithDefault.Type, propWithDefault, true, writer);
            conventions.WriteInLineDescription(propWithDefault.Documentation.Description, writer);
            if (parentClass.IsOfKind(CodeClassKind.Model))
            {
                writer.WriteLine($"{propWithDefault.Name.ToSnakeCase()}: {(propWithDefault.Type.IsNullable ? "Optional[" : string.Empty)}{returnType}{(propWithDefault.Type.IsNullable ? "]" : string.Empty)} = {propWithDefault.DefaultValue}");
                writer.WriteLine();
            }
            else
            {
                writer.WriteLine($"self.{conventions.GetAccessModifier(propWithDefault.Access)}{propWithDefault.NamePrefix}{propWithDefault.Name.ToSnakeCase()}: {(propWithDefault.Type.IsNullable ? "Optional[" : string.Empty)}{returnType}{(propWithDefault.Type.IsNullable ? "]" : string.Empty)} = {propWithDefault.DefaultValue}");
                writer.WriteLine();
            }
        }
    }
    private void WriteSetterAccessProperties(CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var propWithDefault in parentClass.GetPropertiesOfKind(SetterAccessProperties)
                                        .Where(static x => !string.IsNullOrEmpty(x.DefaultValue))
                                        // do not apply the default value if the type is composed as the default value may not necessarily which type to use
                                        .Where(static x => x.Type is not CodeType propType || propType.TypeDefinition is not CodeClass propertyClass || propertyClass.OriginalComposedType is null)
                                        .OrderByDescending(static x => x.Kind)
                                        .ThenBy(static x => x.Name))
        {
            if (parentClass.IsOfKind(CodeClassKind.Model))
                writer.WriteLine($"{propWithDefault.Name.ToSnakeCase()} = {propWithDefault.DefaultValue}");
            else
                writer.WriteLine($"self.{propWithDefault.Name.ToSnakeCase()} = {propWithDefault.DefaultValue}");
        }
    }
    private void WriteSetterAccessPropertiesWithoutDefaults(CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var propWithoutDefault in parentClass.GetPropertiesOfKind(SetterAccessProperties)
                                        .Where(static x => string.IsNullOrEmpty(x.DefaultValue))
                                        .OrderByDescending(static x => x.Kind)
                                        .ThenBy(static x => x.Name))
        {
            var returnType = conventions.GetTypeString(propWithoutDefault.Type, propWithoutDefault, true, writer);
            conventions.WriteInLineDescription(propWithoutDefault.Documentation.Description, writer);
            if (parentClass.IsOfKind(CodeClassKind.Model))
                writer.WriteLine($"{propWithoutDefault.Name.ToSnakeCase()}: {(propWithoutDefault.Type.IsNullable ? "Optional[" : string.Empty)}{returnType}{(propWithoutDefault.Type.IsNullable ? "]" : string.Empty)} = None");
            else
                writer.WriteLine($"self.{conventions.GetAccessModifier(propWithoutDefault.Access)}{propWithoutDefault.NamePrefix}{propWithoutDefault.Name.ToSnakeCase()}: {(propWithoutDefault.Type.IsNullable ? "Optional[" : string.Empty)}{returnType}{(propWithoutDefault.Type.IsNullable ? "]" : string.Empty)} = None");
        }
    }
    private static void AssignPropertyFromParameter(CodeClass parentClass, CodeMethod currentMethod, CodeParameterKind parameterKind, CodePropertyKind propertyKind, LanguageWriter writer, string? variableName = default)
    {
        if (parentClass.GetPropertyOfKind(propertyKind) is CodeProperty property)
        {
            if (!string.IsNullOrEmpty(variableName))
                writer.WriteLine($"self.{property.Name.ToSnakeCase()} = {variableName.ToSnakeCase()}");
            else if (currentMethod.Parameters.OfKind(parameterKind) is CodeParameter parameter)
                writer.WriteLine($"self.{property.Name.ToSnakeCase()} = {parameter.Name.ToSnakeCase()}");
        }
    }
    private static void WriteSetterBody(CodeMethod codeElement, LanguageWriter writer, CodeClass parentClass)
    {
        if (!parentClass.IsOfKind(CodeClassKind.Model))
        {
            var backingStore = parentClass.GetBackingStoreProperty();
            if (backingStore == null)
                writer.WriteLine($"self.{codeElement.AccessedProperty?.NamePrefix}{codeElement.AccessedProperty?.Name?.ToSnakeCase()} = value");
            else
                writer.WriteLine($"self.{backingStore.NamePrefix}{backingStore.Name.ToSnakeCase()}[\"{codeElement.AccessedProperty?.Name?.ToSnakeCase()}\"] = value");
            writer.CloseBlock(string.Empty);
        }
        else
            writer.DecreaseIndent();
    }
    private void WriteGetterBody(CodeMethod codeElement, LanguageWriter writer, CodeClass parentClass)
    {
        if (!parentClass.IsOfKind(CodeClassKind.Model))
        {
            var backingStore = parentClass.GetBackingStoreProperty();
            if (backingStore == null)
                writer.WriteLine($"return self.{codeElement.AccessedProperty?.NamePrefix}{codeElement.AccessedProperty?.Name?.ToSnakeCase()}");
            else
                if (!(codeElement.AccessedProperty?.Type?.IsNullable ?? true) &&
                    !(codeElement.AccessedProperty?.ReadOnly ?? true) &&
                    !string.IsNullOrEmpty(codeElement.AccessedProperty?.DefaultValue))
            {
                writer.WriteLines($"value: {conventions.GetTypeString(codeElement.AccessedProperty.Type, codeElement, true, writer)} = self.{backingStore.NamePrefix}{backingStore.Name.ToSnakeCase()}.get(\"{codeElement.AccessedProperty.Name.ToSnakeCase()}\")",
                    "if not value:");
                writer.IncreaseIndent();
                writer.WriteLines($"value = {codeElement.AccessedProperty.DefaultValue}",
                    $"self.{codeElement.AccessedProperty?.NamePrefix}{codeElement.AccessedProperty?.Name?.ToSnakeCase()} = value");
                writer.DecreaseIndent();
                writer.WriteLines("return value");
            }
            else
                writer.WriteLine($"return self.{backingStore.NamePrefix}{backingStore.Name.ToSnakeCase()}.get(\"{codeElement.AccessedProperty?.Name?.ToSnakeCase()}\")");
            writer.CloseBlock(string.Empty);
        }
        else
            writer.DecreaseIndent();
    }
    private static void WriteDefaultMethodBody(CodeMethod codeElement, LanguageWriter writer, string returnType)
    {
        var promisePrefix = codeElement.IsAsync ? "await " : string.Empty;
        writer.WriteLine($"return {promisePrefix}{returnType}()");
    }
    private const string DefaultDeserializerValue = "{}";
    private void WriteDeserializerBody(CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer, bool inherits)
    {
        _codeUsingWriter.WriteInternalImports(parentClass, writer);
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
            WriteDeserializerBodyForUnionModel(codeElement, parentClass, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteDeserializerBodyForIntersectionModel(parentClass, writer);
        else
            WriteDeserializerBodyForInheritedModel(inherits, codeElement, parentClass, writer);
    }
    private void WriteDeserializerBodyForUnionModel(CodeMethod method, CodeClass parentClass, LanguageWriter writer)
    {
        foreach (var otherPropName in parentClass
                                        .GetPropertiesOfKind(CodePropertyKind.Custom)
                                        .Where(static x => !x.ExistsInBaseType)
                                        .Where(static x => x.Type is CodeType propertyType && !propertyType.IsCollection && propertyType.TypeDefinition is CodeClass)
                                        .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                        .ThenBy(static x => x.Name)
                                        .Select(static x => x.Name))
        {
            writer.StartBlock($"if self.{otherPropName.ToSnakeCase()}:");
            writer.WriteLine($"return self.{otherPropName.ToSnakeCase()}.{method.Name.ToSnakeCase()}()");
            writer.DecreaseIndent();
        }
        writer.WriteLine($"return {DefaultDeserializerValue}");
    }
    private void WriteDeserializerBodyForIntersectionModel(CodeClass parentClass, LanguageWriter writer)
    {
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType propType && propType.TypeDefinition is CodeClass && !x.Type.IsCollection)
                                            .ToArray();
        if (complexProperties.Any())
        {
            var propertiesNames = complexProperties
                                .Select(static x => x.Name.ToSnakeCase())
                                .OrderBy(static x => x)
                                .ToArray();
            var propertiesNamesAsConditions = propertiesNames
                                .Select(static x => $"{x}")
                                .Aggregate(static (x, y) => $"self.{x} or self.{y}");
            writer.StartBlock($"if {propertiesNamesAsConditions}:");
            var propertiesNamesAsArgument = propertiesNames
                                .Aggregate(static (x, y) => $"self.{x}, self.{y}");
            writer.WriteLine($"return ParseNodeHelper.merge_deserializers_for_intersection_wrapper({propertiesNamesAsArgument})");
            writer.DecreaseIndent();
        }
        writer.WriteLine($"return {DefaultDeserializerValue}");
    }
    private void WriteDeserializerBodyForInheritedModel(bool inherits, CodeMethod codeElement, CodeClass parentClass, LanguageWriter writer)
    {
        _codeUsingWriter.WriteInternalImports(parentClass, writer);
        writer.StartBlock("fields: Dict[str, Callable[[Any], None]] = {");
        foreach (var otherProp in parentClass
                                        .GetPropertiesOfKind(CodePropertyKind.Custom)
                                        .Where(static x => !x.ExistsInBaseType)
                                        .OrderBy(static x => x.Name))
        {
            writer.WriteLine($"\"{otherProp.WireName}\": lambda n : setattr(self, '{otherProp.Name.ToSnakeCase()}', n.{GetDeserializationMethodName(otherProp.Type, codeElement, parentClass)}),");
        }
        writer.CloseBlock();
        if (inherits)
        {
            writer.WriteLine($"super_fields = super().{codeElement.Name.ToSnakeCase()}()");
            writer.WriteLine("fields.update(super_fields)");
        }
        writer.WriteLine("return fields");
    }
    private void WriteRequestExecutorBody(CodeMethod codeElement, RequestParams requestParams, CodeClass parentClass, bool isVoid, string returnType, LanguageWriter writer)
    {
        if (codeElement.HttpMethod == null) throw new InvalidOperationException("http method cannot be null");

        var generatorMethodName = parentClass
                                            .Methods
                                            .FirstOrDefault(x => x.IsOfKind(CodeMethodKind.RequestGenerator) && x.HttpMethod == codeElement.HttpMethod)
                                            ?.Name
                                            ?.ToSnakeCase();
        writer.WriteLine($"request_info = self.{generatorMethodName}(");
        var requestInfoParameters = new[] { requestParams.requestBody, requestParams.requestConfiguration }
                                        .Where(static x => x != null)
                                        .Select(static x => x!.Name.ToSnakeCase())
                                        .ToArray();
        if (requestInfoParameters.Any() && requestInfoParameters.Aggregate(static (x, y) => $"{x}, {y}") is string parameters)
        {
            writer.IncreaseIndent();
            writer.WriteLine(parameters);
            writer.DecreaseIndent();
        }
        writer.WriteLine(")");
        var isStream = conventions.StreamTypeName.Equals(returnType, StringComparison.OrdinalIgnoreCase);
        var returnTypeWithoutCollectionSymbol = GetReturnTypeWithoutCollectionSymbol(codeElement, returnType);
        var genericTypeForSendMethod = GetSendRequestMethodName(isVoid, isStream, codeElement.ReturnType.IsCollection, returnTypeWithoutCollectionSymbol);
        var newFactoryParameter = GetTypeFactory(isVoid, isStream, returnTypeWithoutCollectionSymbol);
        var errorMappingVarName = "None";
        if (codeElement.ErrorMappings.Any())
        {
            _codeUsingWriter.WriteInternalErrorMappingImports(parentClass, writer);
            errorMappingVarName = "error_mapping";
            writer.StartBlock($"{errorMappingVarName}: Dict[str, ParsableFactory] = {{");
            foreach (var errorMapping in codeElement.ErrorMappings)
            {
                writer.WriteLine($"\"{errorMapping.Key.ToUpperInvariant()}\": {errorMapping.Value.Name.ToFirstCharacterUpperCase()},");
            }
            writer.CloseBlock();
        }
        writer.StartBlock("if not self.request_adapter:");
        writer.WriteLine("raise Exception(\"Http core is null\") ");
        writer.DecreaseIndent();
        _codeUsingWriter.WriteDeferredImport(parentClass, codeElement.ReturnType.Name, writer);
        writer.WriteLine($"return await self.request_adapter.{genericTypeForSendMethod}(request_info,{newFactoryParameter} {errorMappingVarName})");
    }
    private string GetReturnTypeWithoutCollectionSymbol(CodeMethod codeElement, string fullTypeName)
    {
        if (!codeElement.ReturnType.IsCollection) return fullTypeName;
        if (codeElement.ReturnType.Clone() is CodeTypeBase clone)
        {
            clone.CollectionKind = CodeTypeBase.CodeTypeCollectionKind.None;
            return conventions.GetTypeString(clone, codeElement);
        }
        return string.Empty;
    }
    private const string RequestInfoVarName = "request_info";
    private void WriteRequestGeneratorBody(CodeMethod codeElement, RequestParams requestParams, CodeClass currentClass, LanguageWriter writer)
    {
        if (codeElement.HttpMethod == null) throw new InvalidOperationException("http method cannot be null");

        writer.WriteLine($"{RequestInfoVarName} = RequestInformation()");
        if (currentClass.GetPropertyOfKind(CodePropertyKind.PathParameters) is CodeProperty urlTemplateParamsProperty &&
            currentClass.GetPropertyOfKind(CodePropertyKind.UrlTemplate) is CodeProperty urlTemplateProperty)
            writer.WriteLines($"{RequestInfoVarName}.url_template = {GetPropertyCall(urlTemplateProperty, "''")}",
                                $"{RequestInfoVarName}.path_parameters = {GetPropertyCall(urlTemplateParamsProperty, "''")}");
        writer.WriteLine($"{RequestInfoVarName}.http_method = Method.{codeElement.HttpMethod.Value.ToString().ToUpperInvariant()}");
        if (codeElement.AcceptedResponseTypes.Any())
            writer.WriteLine($"{RequestInfoVarName}.headers[\"Accept\"] = [\"{string.Join(", ", codeElement.AcceptedResponseTypes)}\"]");
        UpdateRequestInformationFromRequestConfiguration(requestParams, writer);
        if (currentClass.GetPropertyOfKind(CodePropertyKind.RequestAdapter) is CodeProperty requestAdapterProperty)
            UpdateRequestInformationFromRequestBody(codeElement, requestParams, requestAdapterProperty, writer);
        writer.WriteLine($"return {RequestInfoVarName}");
    }
    private static string GetPropertyCall(CodeProperty property, string defaultValue) => property == null ? defaultValue : $"self.{property.Name.ToSnakeCase()}";
    private void WriteSerializerBody(bool inherits, CodeClass parentClass, LanguageWriter writer)
    {
        if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForUnionType)
            WriteSerializerBodyForUnionModel(parentClass, writer);
        else if (parentClass.DiscriminatorInformation.ShouldWriteDiscriminatorForIntersectionType)
            WriteSerializerBodyForIntersectionModel(parentClass, writer);
        else
            WriteSerializerBodyForInheritedModel(inherits, parentClass, writer);

        if (parentClass.GetPropertyOfKind(CodePropertyKind.AdditionalData) is CodeProperty additionalDataProperty)
            writer.WriteLine($"writer.write_additional_data_value(self.{additionalDataProperty.Name.ToSnakeCase()})");
    }
    private void WriteSerializerBodyForInheritedModel(bool inherits, CodeClass parentClass, LanguageWriter writer)
    {
        if (inherits)
            writer.WriteLine("super().serialize(writer)");
        foreach (var otherProp in parentClass
                                        .GetPropertiesOfKind(CodePropertyKind.Custom)
                                        .Where(static x => !x.ExistsInBaseType && !x.ReadOnly)
                                        .OrderBy(static x => x.Name))
        {
            var serializationMethodName = GetSerializationMethodName(otherProp.Type);
            writer.WriteLine($"writer.{serializationMethodName}(\"{otherProp.WireName}\", self.{otherProp.Name.ToSnakeCase()})");
        }
    }
    private void WriteSerializerBodyForUnionModel(CodeClass parentClass, LanguageWriter writer)
    {
        var includeElse = false;
        foreach (var otherProp in parentClass
                                        .GetPropertiesOfKind(CodePropertyKind.Custom)
                                        .Where(static x => !x.ExistsInBaseType)
                                        .OrderBy(static x => x, CodePropertyTypeForwardComparer)
                                        .ThenBy(static x => x.Name))
        {
            writer.StartBlock($"{(includeElse ? "el" : string.Empty)}if self.{otherProp.Name.ToSnakeCase()}:");
            writer.WriteLine($"writer.{GetSerializationMethodName(otherProp.Type)}(None, self.{otherProp.Name.ToSnakeCase()})");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
    }
    private void WriteSerializerBodyForIntersectionModel(CodeClass parentClass, LanguageWriter writer)
    {
        var includeElse = false;
        foreach (var otherProp in parentClass
                                        .GetPropertiesOfKind(CodePropertyKind.Custom)
                                        .Where(static x => !x.ExistsInBaseType)
                                        .Where(static x => x.Type is not CodeType propertyType || propertyType.IsCollection || propertyType.TypeDefinition is not CodeClass)
                                        .OrderBy(static x => x, CodePropertyTypeBackwardComparer)
                                        .ThenBy(static x => x.Name))
        {
            writer.StartBlock($"{(includeElse ? "el" : string.Empty)}if self.{otherProp.Name.ToSnakeCase()}:");
            writer.WriteLine($"writer.{GetSerializationMethodName(otherProp.Type)}(None, self.{otherProp.Name.ToSnakeCase()})");
            writer.DecreaseIndent();
            if (!includeElse)
                includeElse = true;
        }
        var complexProperties = parentClass.GetPropertiesOfKind(CodePropertyKind.Custom)
                                            .Where(static x => x.Type is CodeType propType && propType.TypeDefinition is CodeClass && !x.Type.IsCollection)
                                            .ToArray();
        if (complexProperties.Any())
        {
            if (includeElse)
            {
                writer.StartBlock("else:");
            }
            var propertiesNames = complexProperties
                                .Select(static x => x.Name.ToSnakeCase())
                                .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
                                .Aggregate(static (x, y) => $"self.{x}, self.{y}");
            writer.WriteLine($"writer.{GetSerializationMethodName(complexProperties[0].Type)}(None, {propertiesNames})");
            if (includeElse)
            {
                writer.DecreaseIndent();
            }
        }
    }

    private void WriteMethodDocumentation(CodeMethod code, LanguageWriter writer, string returnType, bool isVoid)
    {
        var isDescriptionPresent = !string.IsNullOrEmpty(code.Documentation.Description);
        var parametersWithDescription = code.Parameters
                                        .Where(static x => !string.IsNullOrEmpty(x.Documentation.Description))
                                        .ToArray();
        var nullablePrefix = code.ReturnType.IsNullable && !isVoid ? "Optional[" : string.Empty;
        var nullableSuffix = code.ReturnType.IsNullable && !isVoid ? "]" : string.Empty;
        if (isDescriptionPresent || parametersWithDescription.Any())
        {
            writer.WriteLine(conventions.DocCommentStart);
            if (isDescriptionPresent)
                writer.WriteLine($"{conventions.DocCommentPrefix}{PythonConventionService.RemoveInvalidDescriptionCharacters(code.Documentation.Description)}");
            if (parametersWithDescription.Any())
            {
                writer.StartBlock("Args:");

                foreach (var paramWithDescription in parametersWithDescription.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    writer.WriteLine($"{conventions.DocCommentPrefix}{paramWithDescription.Name.ToSnakeCase()}: {PythonConventionService.RemoveInvalidDescriptionCharacters(paramWithDescription.Documentation.Description)}");
                writer.DecreaseIndent();
            }
            if (!isVoid)
                writer.WriteLine($"{conventions.DocCommentPrefix}Returns: {nullablePrefix}{returnType}{nullableSuffix}");
            writer.WriteLine(conventions.DocCommentEnd);
        }
    }
    private static readonly PythonCodeParameterOrderComparer parameterOrderComparer = new();
    private void WriteMethodPrototype(CodeMethod code, LanguageWriter writer, string returnType, bool isVoid)
    {
        if (code.IsOfKind(CodeMethodKind.Factory))
            writer.WriteLine("@staticmethod");
        var accessModifier = conventions.GetAccessModifier(code.Access);
        var isConstructor = code.IsOfKind(CodeMethodKind.Constructor, CodeMethodKind.ClientConstructor);
        var methodName = (code.Kind switch
        {
            _ when code.IsAccessor => code.AccessedProperty?.Name,
            _ when isConstructor => "__init__",
            _ => code.Name,
        })?.ToSnakeCase();
        var asyncPrefix = code.IsAsync && code.Kind is CodeMethodKind.RequestExecutor ? "async " : string.Empty;
        var instanceReference = code.IsOfKind(CodeMethodKind.Factory) ? string.Empty : "self,";
        var parameters = string.Join(", ", code.Parameters.OrderBy(x => x, parameterOrderComparer)
                                                        .Select(p => new PythonConventionService() // requires a writer instance because method parameters use inline type definitions
                                                        .GetParameterSignature(p, code, writer))
                                                        .ToList());
        var nullablePrefix = code.ReturnType.IsNullable && !isVoid ? "Optional[" : string.Empty;
        var nullableSuffix = code.ReturnType.IsNullable && !isVoid ? "]" : string.Empty;
        var propertyDecorator = code.Kind switch
        {
            CodeMethodKind.Getter => "@property",
            CodeMethodKind.Setter => $"@{methodName}.setter",
            _ => string.Empty
        };
        var nullReturnTypeSuffix = !isVoid && !isConstructor;
        var returnTypeSuffix = nullReturnTypeSuffix ? $"{nullablePrefix}{returnType}{nullableSuffix}" : "None";
        if (!string.IsNullOrEmpty(propertyDecorator))
            writer.WriteLine($"{propertyDecorator}");
        writer.WriteLine($"{asyncPrefix}def {accessModifier}{methodName}({instanceReference}{parameters}) -> {returnTypeSuffix}:");
    }
    private string GetDeserializationMethodName(CodeTypeBase propType, CodeMethod codeElement, CodeClass parentClass)
    {
        var isCollection = propType.CollectionKind != CodeTypeBase.CodeTypeCollectionKind.None;
        var propertyType = conventions.TranslateType(propType);
        if (conventions.TypeExistInSameClassAsTarget(propType, codeElement))
            propertyType = parentClass.Name.ToFirstCharacterUpperCase();
        if (propType is CodeType currentType)
        {
            if (currentType.TypeDefinition is CodeEnum currentEnum)
                return $"get_{(currentEnum.Flags || isCollection ? "collection_of_enum_values" : "enum_value")}({propertyType.ToCamelCase()})";
            if (isCollection)
                if (currentType.TypeDefinition == null)
                    return $"get_collection_of_primitive_values({propertyType})";
                else
                    return $"get_collection_of_object_values({propertyType.ToCamelCase()})";
        }
        return propertyType switch
        {
            "str" or "bool" or "int" or "float" or "UUID" or "bytes" => $"get_{propertyType.ToLowerInvariant()}_value()",
            "datetime.datetime" => "get_datetime_value()",
            "datetime.date" => "get_date_value()",
            "datetime.time" => "get_time_value()",
            "datetime.timedelta" => "get_timedelta_value()",
            _ => $"get_object_value({propertyType.ToCamelCase()})",
        };
    }
    private string GetSerializationMethodName(CodeTypeBase propType)
    {
        var isCollection = propType.CollectionKind != CodeTypeBase.CodeTypeCollectionKind.None;
        var propertyType = conventions.TranslateType(propType);
        if (propType is CodeType currentType)
        {
            if (isCollection)
                if (currentType.TypeDefinition == null)
                    return "write_collection_of_primitive_values";
                else if (currentType.TypeDefinition is CodeEnum)
                    return "write_collection_of_enum_values";
                else
                    return "write_collection_of_object_values";
            else if (currentType.TypeDefinition is CodeEnum)
                return "write_enum_value";
        }
        return propertyType switch
        {
            "str" or "bool" or "int" or "float" or "UUID" or "bytes" => $"write_{propertyType.ToLowerInvariant()}_value",
            "datetime.datetime" => "write_datetime_value",
            "datetime.date" => "write_date_value",
            "datetime.time" => "write_time_value",
            "datetime.timedelta" => "write_timedelta_value",
            _ => "write_object_value",
        };
    }
    private string GetTypeFactory(bool isVoid, bool isStream, string returnType)
    {
        if (isVoid) return string.Empty;
        if (isStream || conventions.IsPrimitiveType(returnType)) return $" \"{returnType}\",";
        return $" {returnType},";
    }
    private string GetSendRequestMethodName(bool isVoid, bool isStream, bool isCollection, string returnType)
    {
        if (isVoid) return "send_no_response_content_async";
        if (isCollection)
        {
            if (conventions.IsPrimitiveType(returnType)) return "send_collection_of_primitive_async";
            return $"send_collection_async";
        }

        if (isStream || conventions.IsPrimitiveType(returnType)) return "send_primitive_async";
        return "send_async";
    }

    private static void UpdateRequestInformationFromRequestConfiguration(RequestParams requestParams, LanguageWriter writer)
    {
        if (requestParams.requestConfiguration != null)
        {
            writer.StartBlock($"if {requestParams.requestConfiguration.Name.ToSnakeCase()}:");
            var headers = requestParams.Headers?.Name.ToSnakeCase() ?? "headers";
            writer.WriteLine($"{RequestInfoVarName}.add_request_headers({requestParams.requestConfiguration.Name.ToSnakeCase()}.{headers})");
            var queryString = requestParams.QueryParameters;
            if (queryString != null)
                writer.WriteLines($"{RequestInfoVarName}.set_query_string_parameters_from_raw_object({requestParams.requestConfiguration.Name.ToSnakeCase()}.{queryString.Name.ToSnakeCase()})");
            var options = requestParams.Options?.Name.ToSnakeCase() ?? "options";
            writer.WriteLine($"{RequestInfoVarName}.add_request_options({requestParams.requestConfiguration.Name.ToSnakeCase()}.{options})");
            writer.DecreaseIndent();
        }
    }

    private void UpdateRequestInformationFromRequestBody(CodeMethod codeElement, RequestParams requestParams, CodeProperty requestAdapterProperty, LanguageWriter writer)
    {
        if (requestParams.requestBody != null)
        {
            if (requestParams.requestBody.Type.Name.Equals(conventions.StreamTypeName, StringComparison.OrdinalIgnoreCase))
                writer.WriteLine($"{RequestInfoVarName}.set_stream_content({requestParams.requestBody.Name.ToSnakeCase()})");
            else
            {
                var setMethodName = requestParams.requestBody.Type is CodeType bodyType && bodyType.TypeDefinition is CodeClass ? "set_content_from_parsable" : "set_content_from_scalar";
                writer.WriteLine($"{RequestInfoVarName}.{setMethodName}(self.{requestAdapterProperty.Name.ToSnakeCase()}, \"{codeElement.RequestBodyContentType}\", {requestParams.requestBody.Name})");
            }
        }
    }
}
