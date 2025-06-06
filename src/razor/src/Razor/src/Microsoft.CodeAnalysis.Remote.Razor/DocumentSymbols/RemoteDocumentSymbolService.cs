﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.ExternalAccess.Razor;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.CodeAnalysis.Razor.Protocol.DocumentSymbols;
using Microsoft.CodeAnalysis.Razor.Remote;
using Microsoft.CodeAnalysis.Remote.Razor.ProjectSystem;
using ExternalHandlers = Microsoft.CodeAnalysis.ExternalAccess.Razor.Cohost.Handlers;

namespace Microsoft.CodeAnalysis.Remote.Razor;

internal sealed partial class RemoteDocumentSymbolService(in ServiceArgs args) : RazorDocumentServiceBase(in args), IRemoteDocumentSymbolService
{
    internal sealed class Factory : FactoryBase<IRemoteDocumentSymbolService>
    {
        protected override IRemoteDocumentSymbolService CreateService(in ServiceArgs args)
            => new RemoteDocumentSymbolService(in args);
    }

    private readonly IDocumentSymbolService _documentSymbolService = args.ExportProvider.GetExportedValue<IDocumentSymbolService>();
    private readonly IClientCapabilitiesService _clientCapabilitiesService = args.ExportProvider.GetExportedValue<IClientCapabilitiesService>();

    public ValueTask<SumType<DocumentSymbol[], SymbolInformation[]>?> GetDocumentSymbolsAsync(JsonSerializableRazorPinnedSolutionInfoWrapper solutionInfo, JsonSerializableDocumentId razorDocumentId, bool useHierarchicalSymbols, CancellationToken cancellationToken)
        => RunServiceAsync(
            solutionInfo,
            razorDocumentId,
            context => GetDocumentSymbolsAsync(context, useHierarchicalSymbols, cancellationToken),
            cancellationToken);

    private async ValueTask<SumType<DocumentSymbol[], SymbolInformation[]>?> GetDocumentSymbolsAsync(RemoteDocumentContext context, bool useHierarchicalSymbols, CancellationToken cancellationToken)
    {
        var generatedDocument = await context.Snapshot
            .GetGeneratedDocumentAsync(cancellationToken)
            .ConfigureAwait(false);

        var csharpSymbols = await ExternalHandlers.DocumentSymbols.GetDocumentSymbolsAsync(
            generatedDocument,
            useHierarchicalSymbols,
            supportsVSExtensions: _clientCapabilitiesService.ClientCapabilities.SupportsVisualStudioExtensions,
            cancellationToken).ConfigureAwait(false);

        var codeDocument = await context.GetCodeDocumentAsync(cancellationToken).ConfigureAwait(false);
        var csharpDocument = codeDocument.GetCSharpDocument();

        return _documentSymbolService.GetDocumentSymbols(context.Uri, csharpDocument, csharpSymbols);
    }
}
