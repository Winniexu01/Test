﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.CodeGeneration;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Utilities;
using Microsoft.CodeAnalysis.OrganizeImports;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CSharp.OrganizeImports;

internal partial class CSharpOrganizeImportsService
{
    private sealed class Rewriter(OrganizeImportsOptions options) : CSharpSyntaxRewriter
    {
        private readonly bool _placeSystemNamespaceFirst = options.PlaceSystemNamespaceFirst;
        private readonly bool _separateGroups = options.SeparateImportDirectiveGroups;
        private readonly SyntaxTrivia _fallbackTrivia = CSharpSyntaxGeneratorInternal.Instance.EndOfLine(options.NewLine);

        public readonly IList<TextChange> TextChanges = [];

        public override SyntaxNode VisitCompilationUnit(CompilationUnitSyntax node)
        {
            node = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            UsingsAndExternAliasesOrganizer.Organize(
                node.Externs, node.Usings,
                _placeSystemNamespaceFirst, _separateGroups,
                _fallbackTrivia,
                out var organizedExternAliasList, out var organizedUsingList);

            var result = node.WithExterns(organizedExternAliasList).WithUsings(organizedUsingList);
            if (node != result)
            {
                AddTextChange(node.Externs, organizedExternAliasList);
                AddTextChange(node.Usings, organizedUsingList);
            }

            return result;
        }

        public override SyntaxNode VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
            => VisitBaseNamespaceDeclaration((BaseNamespaceDeclarationSyntax?)base.VisitFileScopedNamespaceDeclaration(node));

        public override SyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            => VisitBaseNamespaceDeclaration((BaseNamespaceDeclarationSyntax?)base.VisitNamespaceDeclaration(node));

        private BaseNamespaceDeclarationSyntax VisitBaseNamespaceDeclaration(BaseNamespaceDeclarationSyntax? node)
        {
            Contract.ThrowIfNull(node);
            UsingsAndExternAliasesOrganizer.Organize(
                node.Externs, node.Usings,
                _placeSystemNamespaceFirst, _separateGroups,
                _fallbackTrivia,
                out var organizedExternAliasList, out var organizedUsingList);

            var result = node.WithExterns(organizedExternAliasList).WithUsings(organizedUsingList);
            if (node != result)
            {
                AddTextChange(node.Externs, organizedExternAliasList);
                AddTextChange(node.Usings, organizedUsingList);
            }

            return result;
        }

        private void AddTextChange<TSyntax>(SyntaxList<TSyntax> list, SyntaxList<TSyntax> organizedList)
            where TSyntax : SyntaxNode
        {
            if (list.Count > 0)
                this.TextChanges.Add(new TextChange(GetTextSpan(list), GetNewText(organizedList)));
        }

        private static string GetNewText<TSyntax>(SyntaxList<TSyntax> organizedList) where TSyntax : SyntaxNode
            => string.Join(string.Empty, organizedList.Select(t => t.ToFullString()));

        private static TextSpan GetTextSpan<TSyntax>(SyntaxList<TSyntax> list) where TSyntax : SyntaxNode
            => TextSpan.FromBounds(list.First().FullSpan.Start, list.Last().FullSpan.End);
    }
}
