﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Kiota.Builder.Extensions;

namespace Kiota.Builder.CodeDOM;

public enum CodeClassKind
{
    Custom,
    RequestBuilder,
    Model,
    QueryParameters,
    /// <summary>
    /// A single parameter to be provided by the SDK user which will contain query parameters, request body, options, etc.
    /// Only used for languages that do not support overloads or optional parameters like go.
    /// </summary>
    ParameterSet,
    /// <summary>
    /// A class used as a placeholder for the barrel file.
    /// </summary>
    BarrelInitializer,
    /// <summary>
    /// Configuration for the request to be sent with the headers, query parameters, and middleware options
    /// </summary>
    RequestConfiguration,
}
/// <summary>
/// CodeClass represents an instance of a Class to be generated in source code
/// </summary>
public class CodeClass : ProprietableBlock<CodeClassKind, ClassDeclaration>, ITypeDefinition, IDiscriminatorInformationHolder, IDeprecableElement
{
    public bool IsErrorDefinition
    {
        get; set;
    }

    /// <summary>
    /// Original composed type this class was generated for.
    /// </summary>
    public CodeComposedTypeBase? OriginalComposedType
    {
        get; set;
    }
    public CodeIndexer? Indexer
    {
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (InnerChildElements.Values.OfType<CodeIndexer>().Any() || InnerChildElements.Values.OfType<CodeMethod>().Any(static x => x.IsOfKind(CodeMethodKind.IndexerBackwardCompatibility)))
            {
                if (Indexer is CodeIndexer existingIndexer)
                {
                    RemoveChildElement(existingIndexer);
                    AddRange(CodeMethod.FromIndexer(existingIndexer, static x => $"With{x.ToFirstCharacterUpperCase()}", static x => x.ToFirstCharacterUpperCase(), true));
                }
                AddRange(CodeMethod.FromIndexer(value, static x => $"With{x.ToFirstCharacterUpperCase()}", static x => x.ToFirstCharacterUpperCase(), false));
            }
            else
                AddRange(value);
        }
        get => InnerChildElements.Values.OfType<CodeIndexer>().FirstOrDefault();
    }
    private CodeProperty? FindPropertyByNameInTypeHierarchy(string propertyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        if (FindChildByName<CodeProperty>(propertyName, findInChildElements: false) is CodeProperty result)
        {
            return result;
        }
        if (BaseClass is CodeClass currentParentClass)
        {
            return currentParentClass.FindPropertyByNameInTypeHierarchy(propertyName);
        }
        return default;
    }
    public IEnumerable<CodeClass> AddInnerClass(params CodeClass[] codeClasses)
    {
        if (codeClasses == null || codeClasses.Any(x => x == null))
            throw new ArgumentNullException(nameof(codeClasses));
        if (!codeClasses.Any())
            throw new ArgumentOutOfRangeException(nameof(codeClasses));
        return AddRange(codeClasses);
    }
    public IEnumerable<CodeInterface> AddInnerInterface(params CodeInterface[] codeInterfaces)
    {
        if (codeInterfaces == null || codeInterfaces.Any(x => x == null))
            throw new ArgumentNullException(nameof(codeInterfaces));
        if (!codeInterfaces.Any())
            throw new ArgumentOutOfRangeException(nameof(codeInterfaces));
        return AddRange(codeInterfaces);
    }
    public CodeClass? BaseClass => StartBlock.Inherits?.TypeDefinition as CodeClass;
    public bool DerivesFrom(CodeClass codeClass)
    {
        ArgumentNullException.ThrowIfNull(codeClass);
        var parent = BaseClass;
        if (parent == null)
            return false;
        if (parent == codeClass)
            return true;
        return parent.DerivesFrom(codeClass);
    }
    public Collection<CodeClass> GetInheritanceTree(bool currentNamespaceOnly = false, bool includeCurrentClass = true)
    {
        var parentClass = BaseClass;
        if (parentClass == null || (currentNamespaceOnly && parentClass.GetImmediateParentOfType<CodeNamespace>() != GetImmediateParentOfType<CodeNamespace>()))
            if (includeCurrentClass)
                return new() { this };
            else
                return new();
        var result = parentClass.GetInheritanceTree(currentNamespaceOnly);
        result.Add(this);
        return result;
    }
    public CodeClass? GetGreatestGrandparent(CodeClass? startClassToSkip = default)
    {
        var parentClass = BaseClass;
        if (parentClass == null)
            return startClassToSkip != null && startClassToSkip == this ? null : this;
        // we don't want to return the current class if this is the start node in the inheritance tree and doesn't have parent
        return parentClass.GetGreatestGrandparent(startClassToSkip);
    }
    private DiscriminatorInformation? _discriminatorInformation;
    /// <inheritdoc />
    public DiscriminatorInformation DiscriminatorInformation
    {
        get
        {
            if (_discriminatorInformation == null)
                DiscriminatorInformation = new DiscriminatorInformation();
            return _discriminatorInformation!;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureElementsAreChildren(value);
            _discriminatorInformation = value;
        }
    }

    public DeprecationInformation? Deprecation
    {
        get; set;
    }
}
public class ClassDeclaration : ProprietableBlockDeclaration
{
    private CodeType? inherits;
    public CodeType? Inherits
    {
        get => inherits; set
        {
            EnsureElementsAreChildren(value);
            inherits = value;
        }
    }
    public CodeProperty? GetOriginalPropertyDefinedFromBaseType(string propertyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyName);

        if (inherits is CodeType currentInheritsType &&
            !inherits.IsExternal &&
            currentInheritsType.TypeDefinition is CodeClass currentParentClass)
            if (currentParentClass.FindChildByName<CodeProperty>(propertyName, false) is CodeProperty currentProperty && !currentProperty.ExistsInBaseType)
                return currentProperty;
            else
                return currentParentClass.StartBlock.GetOriginalPropertyDefinedFromBaseType(propertyName);
        return default;
    }
    public bool InheritsFrom(CodeClass candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (inherits is CodeType currentInheritsType &&
            currentInheritsType.TypeDefinition is CodeClass currentParentClass)
            if (currentParentClass == candidate)
                return true;
            else
                return currentParentClass.StartBlock.InheritsFrom(candidate);
        return false;
    }
}

