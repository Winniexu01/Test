﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;

namespace Microsoft.CodeAnalysis.GenerateFromMembers;

internal sealed class SelectedMemberInfo(
    INamedTypeSymbol containingType,
    ImmutableArray<SyntaxNode> selectedDeclarations,
    ImmutableArray<ISymbol> selectedMembers)
{
    public readonly INamedTypeSymbol ContainingType = containingType;
    public readonly ImmutableArray<SyntaxNode> SelectedDeclarations = selectedDeclarations;
    public readonly ImmutableArray<ISymbol> SelectedMembers = selectedMembers;
}
