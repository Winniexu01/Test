﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Razor.LanguageServer.EndpointContracts;
using Microsoft.CodeAnalysis.Razor.Protocol.DocumentPresentation;

namespace Microsoft.AspNetCore.Razor.LanguageServer.DocumentPresentation;

[RazorLanguageServerEndpoint(VSInternalMethods.TextDocumentTextPresentationName)]
internal interface ITextDocumentTextPresentationHandler : IRazorRequestHandler<TextPresentationParams, WorkspaceEdit?>, ICapabilitiesProvider
{
}
