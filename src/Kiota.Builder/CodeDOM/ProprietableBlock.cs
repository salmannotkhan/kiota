﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Kiota.Builder.CodeDOM;

/// <summary>
/// Marker interface for type testing
/// </summary>
public interface IProprietableBlock : ICodeElement
{
}

/// <summary>
/// Represents a block of code that can have properties and methods
/// </summary>
public abstract class ProprietableBlock<TBlockKind, TBlockDeclaration> : CodeBlock<TBlockDeclaration, BlockEnd>, IDocumentedElement, IProprietableBlock where TBlockKind : Enum where TBlockDeclaration : ProprietableBlockDeclaration, new()
{
    private string name = string.Empty;
    /// <summary>
    /// Name of Class
    /// </summary>
    public override string Name
    {
        get => name;
        set
        {
            name = value;
            StartBlock.Name = name;
        }
    }
    public CodeDocumentation Documentation { get; set; } = new();
    public IEnumerable<CodeProperty> AddProperty(params CodeProperty[] properties)
    {
        if (properties == null || properties.Any(static x => x == null))
            throw new ArgumentNullException(nameof(properties));
        if (!properties.Any())
            throw new ArgumentOutOfRangeException(nameof(properties));
        return AddRange(properties);
    }
    public void RemovePropertiesOfKind(params CodePropertyKind[] kind)
    {
        if (kind == null || !kind.Any())
            throw new ArgumentNullException(nameof(kind));
        var propertiesToRemove = Properties.Where(x => x.IsOfKind(kind)).ToList();
        foreach (var property in propertiesToRemove)
            RemoveChildElement(property);
    }
#nullable disable
    public TBlockKind Kind
    {
        get; set;
    }
#nullable enable
    public bool IsOfKind(params TBlockKind[] kinds)
    {
        return kinds?.Contains(Kind) ?? false;
    }
    public CodeProperty? GetPropertyOfKind(params CodePropertyKind[] kind) =>
    Properties.FirstOrDefault(x => x.IsOfKind(kind));

    public CodeProperty? GetMethodByAccessedPropertyOfKind(params CodePropertyKind[] kind) =>
        Methods.FirstOrDefault(x => x.AccessedProperty?.IsOfKind(kind) ?? false)?.AccessedProperty;

    public IEnumerable<CodeProperty> Properties => InnerChildElements.Values.OfType<CodeProperty>().OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<CodeProperty> UnorderedProperties => InnerChildElements.Values.OfType<CodeProperty>();
    public IEnumerable<CodeMethod> Methods => InnerChildElements.Values.OfType<CodeMethod>().OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<CodeMethod> UnorderedMethods => InnerChildElements.Values.OfType<CodeMethod>();
    public IEnumerable<CodeClass> InnerClasses => InnerChildElements.Values.OfType<CodeClass>().OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);
    public bool ContainsMember(string name)
    {
        return InnerChildElements.ContainsKey(name);
    }
    public IEnumerable<CodeMethod> AddMethod(params CodeMethod[] methods)
    {
        if (methods == null || methods.Any(static x => x == null))
            throw new ArgumentNullException(nameof(methods));
        if (!methods.Any())
            throw new ArgumentOutOfRangeException(nameof(methods));
        return AddRange(methods);
    }

}

public class ProprietableBlockDeclaration : BlockDeclaration
{
    private readonly ConcurrentDictionary<string, CodeType> implements = new(StringComparer.OrdinalIgnoreCase);
    public void AddImplements(params CodeType[] types)
    {
        if (types == null || types.Any(x => x == null))
            throw new ArgumentNullException(nameof(types));
        EnsureElementsAreChildren(types);
        foreach (var type in types)
            implements.TryAdd(type.Name, type);
    }
    public CodeType? FindImplementByName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return implements.TryGetValue(name, out var type) ? type : null;
    }
    public void ReplaceImplementByName(string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(newName);
        var impl = FindImplementByName(oldName);
        if (impl != null)
        {
            RemoveImplements(impl);
            impl.Name = newName;
            AddImplements(impl);
        }
    }
    public void RemoveImplements(params CodeType[] types)
    {
        if (types == null || types.Any(x => x == null))
            throw new ArgumentNullException(nameof(types));
        foreach (var type in types)
            implements.TryRemove(type.Name, out var _);
    }
    public IEnumerable<CodeType> Implements => implements.Values.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);
}
