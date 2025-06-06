﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.QuickInfo;

internal sealed record OnTheFlyDocsRelevantFileInfo
{
    public Document Document { get; }
    public TextSpan TextSpan { get; }

    public OnTheFlyDocsRelevantFileInfo(Document document, TextSpan textSpan)
    {
        Document = document;
        TextSpan = textSpan;
    }
}

