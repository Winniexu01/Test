﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace System.Windows.Markup.Tests;

public class XamlSetTypeConverterAttributeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xamlSetTypeConverterHandler")]
    public void Ctor_String(string? xamlSetTypeConverterHandler)
    {
        var attribute = new XamlSetTypeConverterAttribute(xamlSetTypeConverterHandler);
        Assert.Equal(xamlSetTypeConverterHandler, attribute.XamlSetTypeConverterHandler);
    }
}
