﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;

namespace Microsoft.AspNetCore.Razor.Language.Components;

internal static class ComponentLayoutDirective
{
    public static readonly DirectiveDescriptor Directive = DirectiveDescriptor.CreateDirective(
        "layout",
        DirectiveKind.SingleLine,
        builder =>
        {
            builder.AddTypeToken(ComponentResources.LayoutDirective_TypeToken_Name, ComponentResources.LayoutDirective_TypeToken_Description);
            builder.Usage = DirectiveUsage.FileScopedSinglyOccurring;
            builder.Description = ComponentResources.LayoutDirective_Description;
        });

    public static void Register(RazorProjectEngineBuilder builder)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.AddDirective(Directive, RazorFileKind.Component, RazorFileKind.ComponentImport);
        builder.Features.Add(new ComponentLayoutDirectivePass());
    }
}
