﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.ExternalAccess.FSharp.Editor;

internal readonly struct FSharpInlineRenameLocation
{
    public Document Document { get; }
    public TextSpan TextSpan { get; }

    public FSharpInlineRenameLocation(Document document, TextSpan textSpan)
    {
        this.Document = document;
        this.TextSpan = textSpan;
    }
}
