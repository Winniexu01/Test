﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Simplification;

namespace Microsoft.CodeAnalysis.QualifyMemberAccess;

internal abstract class AbstractQualifyMemberAccessDiagnosticAnalyzer<
    TLanguageKindEnum,
    TExpressionSyntax,
    TSimpleNameSyntax>
    : AbstractBuiltInCodeStyleDiagnosticAnalyzer
    where TLanguageKindEnum : struct
    where TExpressionSyntax : SyntaxNode
    where TSimpleNameSyntax : TExpressionSyntax
{
    protected AbstractQualifyMemberAccessDiagnosticAnalyzer()
        : base(IDEDiagnosticIds.AddThisOrMeQualificationDiagnosticId,
               EnforceOnBuildValues.AddQualification,
               options:
               [
                   CodeStyleOptions2.QualifyFieldAccess,
                   CodeStyleOptions2.QualifyPropertyAccess,
                   CodeStyleOptions2.QualifyMethodAccess,
                   CodeStyleOptions2.QualifyEventAccess,
               ],
               new LocalizableResourceString(nameof(AnalyzersResources.Member_access_should_be_qualified), AnalyzersResources.ResourceManager, typeof(AnalyzersResources)),
               new LocalizableResourceString(nameof(AnalyzersResources.Add_this_or_Me_qualification), AnalyzersResources.ResourceManager, typeof(AnalyzersResources)))
    {
    }

    /// <summary>
    /// Reports on whether the specified member is suitable for qualification. Some member
    /// access expressions cannot be qualified; for instance if they begin with <c>base.</c>,
    /// <c>MyBase.</c>, or <c>MyClass.</c>.
    /// </summary>
    /// <returns>True if the member access can be qualified; otherwise, False.</returns>
    protected abstract bool CanMemberAccessBeQualified(ISymbol containingSymbol, SyntaxNode node);

    protected abstract bool IsAlreadyQualifiedMemberAccess(TExpressionSyntax node);

    protected override void InitializeWorker(AnalysisContext context)
        => context.RegisterOperationAction(AnalyzeOperation, OperationKind.FieldReference, OperationKind.PropertyReference, OperationKind.MethodReference, OperationKind.Invocation);

    protected abstract Location GetLocation(IOperation operation);
    protected abstract ISimplification Simplification { get; }

    public override DiagnosticAnalyzerCategory GetAnalyzerCategory() => DiagnosticAnalyzerCategory.SemanticSpanAnalysis;

    private void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (context.ContainingSymbol.IsStatic)
        {
            return;
        }

        switch (context.Operation)
        {
            case IMemberReferenceOperation memberReferenceOperation:
                AnalyzeOperation(context, memberReferenceOperation, memberReferenceOperation.Instance);
                break;
            case IInvocationOperation invocationOperation:
                AnalyzeOperation(context, invocationOperation, invocationOperation.Instance);
                break;
            default:
                throw ExceptionUtilities.UnexpectedValue(context.Operation);
        }
    }

    private void AnalyzeOperation(OperationAnalysisContext context, IOperation operation, IOperation? instanceOperation)
    {
        // this is a static reference so we don't care if it's qualified
        if (instanceOperation == null)
            return;

        // if we're not referencing `this.` or `Me.` (e.g., a parameter, local, etc.)
        if (instanceOperation.Kind != OperationKind.InstanceReference)
            return;

        // We shouldn't qualify if it is inside a property pattern
        if (context.Operation.Parent?.Kind == OperationKind.PropertySubpattern)
            return;

        // Initializer lists are IInvocationOperation which if passed to GetApplicableOptionFromSymbolKind
        // will incorrectly fetch the options for method call.
        // We still want to handle InstanceReferenceKind.ContainingTypeInstance
        if ((instanceOperation as IInstanceReferenceOperation)?.ReferenceKind == InstanceReferenceKind.ImplicitReceiver)
            return;

        // If we can't be qualified (e.g., because we're already qualified with `base.`), we're done.
        if (!CanMemberAccessBeQualified(context.ContainingSymbol, instanceOperation.Syntax))
            return;

        // if we can't find a member then we can't do anything.  Also, we shouldn't qualify
        // accesses to static members.  
        if (IsStaticMemberOrIsLocalFunction(operation))
            return;

        if (instanceOperation.Syntax is not TSimpleNameSyntax simpleName)
            return;

        var symbolKind = operation switch
        {
            IMemberReferenceOperation memberReferenceOperation => memberReferenceOperation.Member.Kind,
            IInvocationOperation invocationOperation => invocationOperation.TargetMethod.Kind,
            _ => throw ExceptionUtilities.UnexpectedValue(operation),
        };

        var simplifierOptions = context.GetAnalyzerOptions().GetSimplifierOptions(Simplification);
        if (!simplifierOptions.TryGetQualifyMemberAccessOption(symbolKind, out var optionValue))
            return;

        var shouldOptionBePresent = optionValue.Value;
        if (!shouldOptionBePresent || ShouldSkipAnalysis(context, optionValue.Notification))
        {
            return;
        }

        if (!IsAlreadyQualifiedMemberAccess(simpleName))
        {
            context.ReportDiagnostic(DiagnosticHelper.Create(
                Descriptor,
                GetLocation(operation),
                optionValue.Notification,
                context.Options,
                additionalLocations: null,
                properties: null));
        }
    }

    private static bool IsStaticMemberOrIsLocalFunction(IOperation operation)
    {
        return operation switch
        {
            IMemberReferenceOperation memberReferenceOperation => IsStaticMemberOrIsLocalFunctionHelper(memberReferenceOperation.Member),
            IInvocationOperation invocationOperation => IsStaticMemberOrIsLocalFunctionHelper(invocationOperation.TargetMethod),
            _ => throw ExceptionUtilities.UnexpectedValue(operation),
        };

        static bool IsStaticMemberOrIsLocalFunctionHelper(ISymbol symbol)
        {
            return symbol == null || symbol.IsStatic || symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction };
        }
    }
}
