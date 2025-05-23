﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators;

public sealed class RazorSourceGeneratorComponentTests : RazorSourceGeneratorTestsBase
{
    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10991")]
    public async Task ImportsRazor()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Folder1/_Imports.razor"] = """
                @using MyApp.MyNamespace.AndAnother
                """,
            ["Folder1/Component1.razor"] = """
                @{ var c = new Class1(); }
                """,
            ["Folder2/Component2.razor"] = """
                @{ var c = new Class1(); }
                """,
        }, new()
        {
            ["Class1.cs"] = """
                namespace MyApp.MyNamespace.AndAnother;

                public class Class1 { }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver,
            // Folder2/Component2.razor(1,16): error CS0246: The type or namespace name 'Class1' could not be found (are you missing a using directive or an assembly reference?)
            //  var c = new Class1(); 
            Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "Class1").WithArguments("Class1").WithLocation(1, 16));

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(3, result.GeneratedSources.Length);
        result.VerifyOutputsMatchBaseline();
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10991")]
    public async Task ImportsRazor_WithMarkup()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["_Imports.razor"] = """
                @using System.Net.Http
                <p>test</p>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        result.Diagnostics.Verify(
            // _Imports.razor(2,1): error RZ10003: Markup, code and block directives are not valid in component imports.
            Diagnostic("RZ10003").WithLocation(2, 1));
        Assert.Single(result.GeneratedSources);
        result.VerifyOutputsMatchBaseline();
    }

    [Fact]
    public async Task ImportsRazor_SystemInNamespace()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["System/_Imports.razor"] = """
                @using global::System.Net.Http
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        result.VerifyOutputsMatchBaseline();
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8718")]
    public async Task PartialClass()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <Component2 Param="42" />
                """,
            ["Shared/Component2.razor"] = """
                @inherits ComponentBase

                Value: @(Param + 1)

                @code {
                    [Parameter]
                    public int Param { get; set; }
                }
                """
        }, new()
        {
            ["Component2.razor.cs"] = """
                using Microsoft.AspNetCore.Components;

                namespace MyApp.Shared;

                public partial class Component2 : ComponentBase { }
                """
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = "7.0";
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8718")]
    public async Task PartialClass_NoBaseInCSharp()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <Component2 Param="42" />
                """,
            ["Shared/Component2.razor"] = """
                @inherits ComponentBase

                Value: @(Param + 1)

                @code {
                    [Parameter]
                    public int Param { get; set; }
                }
                """
        }, new()
        {
            ["Component2.razor.cs"] = """
                using Microsoft.AspNetCore.Components;

                namespace MyApp.Shared;

                public partial class Component2 { }
                """
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = "7.0";
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact]
    public async Task Inject()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                @inject IServiceProvider ServiceProvider
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.GeneratedSources);
        result.VerifyOutputsMatchBaseline();
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8718")]
    public async Task ComponentInheritsFromComponent()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                Hello from Component1
                <DerivedComponent />
                """,
            ["Shared/BaseComponent.razor"] = """
                Hello from Base
                """,
            ["Shared/DerivedComponent.razor"] = """
                @inherits BaseComponent
                Hello from Derived
                """
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = "7.0";
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1954771")]
    public async Task EmptyRootNamespace()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<Shared.Component1>(RenderMode.Static))
                @(await Html.RenderComponentAsync<Component3>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                Component1 in Shared namespace
                <Component2 />
                <Component4 />
                """,
            ["Component2.razor"] = """
                Component2 in global namespace
                """,
            ["Component3.razor"] = """
                Component3 in global namespace
                <Shared.Component1 />
                """
        }, new()
        {
            ["Component4.cs"] = """
                using Microsoft.AspNetCore.Components;
                public class Component4 : ComponentBase { }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RootNamespace"] = string.Empty;
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, result.GeneratedSources.Length);
        result.VerifyOutputsMatchBaseline();
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Theory, CombinatorialData]
    public async Task AddComponentParameter(
        [CombinatorialValues("7.0", "8.0", "Latest")] string langVersion)
    {
        // Arrange.
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                <Component1 Param="42" />

                @code {
                    [Parameter]
                    public int Param { get; set; }
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = langVersion;
        });

        // Act.
        var result = RunGenerator(compilation!, ref driver);

        // Assert.
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources);
        if (langVersion == "7.0")
        {
            // In Razor v7, AddComponentParameter shouldn't be used even if available.
            Assert.Contains("AddAttribute", source.SourceText.ToString());
            Assert.DoesNotContain("AddComponentParameter", source.SourceText.ToString());
        }
        else
        {
            Assert.DoesNotContain("AddAttribute", source.SourceText.ToString());
            Assert.Contains("AddComponentParameter", source.SourceText.ToString());
        }
    }

    [Theory, CombinatorialData]
    public async Task AddComponentParameter_InSource(
        [CombinatorialValues("7.0", "8.0", "Latest")] string langVersion)
    {
        // Arrange.
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                <Component1 Param="42" />

                @code {
                    [Parameter]
                    public int Param { get; set; }
                }
                """,
        }, new()
        {
            ["Shims.cs"] = """
                namespace Microsoft.AspNetCore.Components.Rendering
                {
                    public sealed class RenderTreeBuilder
                    {
                        public void OpenComponent<TComponent>(int sequence) { }
                        public void AddAttribute(int sequence, string name, object value) { }
                        public void AddComponentParameter(int sequence, string name, object value) { }
                        public void CloseComponent() { }
                    }
                }
                namespace Microsoft.AspNetCore.Components
                {
                    public sealed class ParameterAttribute : System.Attribute { }
                    public interface IComponent { }
                    public abstract class ComponentBase : IComponent
                    {
                        protected virtual void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) { }
                    }
                }
                namespace Microsoft.AspNetCore.Components.CompilerServices
                {
                    public static class RuntimeHelpers
                    {
                        public static T TypeCheck<T>(T value) => throw null!;
                    }
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();

        // Remove the AspNetCore DLL v7 to avoid clashes.
        var aspnetDll = compilation.References.Single(r => r.Display.EndsWith("Microsoft.AspNetCore.Components.dll", StringComparison.Ordinal));
        compilation = compilation.RemoveReferences(aspnetDll);

        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = langVersion;
        });

        // Act.
        var result = RunGenerator(compilation!, ref driver);

        // Assert. Behaves as if `AddComponentParameter` wasn't available because
        // the source generator only searches for it in references, not the current compilation.
        Assert.Empty(result.Diagnostics);
        var source = Assert.Single(result.GeneratedSources);
        Assert.Contains("AddAttribute", source.SourceText.ToString());
        Assert.DoesNotContain("AddComponentParameter", source.SourceText.ToString());
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8660")]
    public async Task TypeArgumentsCannotBeInferred()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                @typeparam T

                <Component1 />

                @code {
                    private void M1<T1>() { }
                    private void M2()
                    {
                        M1();
                    }
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver,
            // Shared/Component1.razor(9,9): error CS0411: The type arguments for method 'Component1<T>.M1<T1>()' cannot be inferred from the usage. Try specifying the type arguments explicitly.
            //         M1();
            Diagnostic(ErrorCode.ERR_CantInferMethTypeArgs, "M1").WithArguments("MyApp.Shared.Component1<T>.M1<T1>()").WithLocation(9, 9));

        // Assert
        result.Diagnostics.Verify(
            // Shared/Component1.razor(3,1): error RZ10001: The type of component 'Component1' cannot be inferred based on the values provided. Consider specifying the type arguments directly using the following attributes: 'T'.
            // <Component1 />
            Diagnostic("RZ10001").WithLocation(3, 1));
        Assert.Single(result.GeneratedSources);
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_Newline()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <!DOCTYPE html>
                <html>
                <head><title>Test</title></head>
                <body>
                This is a test
                </body>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_Newline_View()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                <!DOCTYPE html>
                <html>
                <head><title>Test</title></head>
                <body>
                This is a test
                </body>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_NoNewline()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <!DOCTYPE html> <html>
                <head><title>Test</title></head>
                <body>
                This is a test
                </body>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_NoNewline_View()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                <!DOCTYPE html> <html>
                <head><title>Test</title></head>
                <body>
                This is a test
                </body>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_HtmlComment()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <!DOCTYPE html> <!-- comment --> <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_HtmlComment_View()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                <!DOCTYPE html> <!-- comment --> <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_RazorComment()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <!DOCTYPE html> @* comment *@ <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_RazorComment_View()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                <!DOCTYPE html> @* comment *@ <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_CSharp()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <!DOCTYPE html> @("from" + " csharp") and HTML <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/8545")]
    public async Task Doctype_CSharp_View()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                <!DOCTYPE html> @("from" + " csharp") and HTML <html>
                </html>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Single(result.GeneratedSources);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/9204")]
    public async Task ScriptTag()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                1: <script>alert('hello')</script>
                2: @{ var c = "alert('hello')"; }<script>@c</script>
                3: <div>alert('hello')</div>
                4: <script>
                alert('hello')</script>
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                Component:
                1: <script>alert('hello')</script>
                2: @{ var c = "alert('hello')"; }<script>@c</script>
                3: <div>alert('hello')</div>
                4: <script>
                alert('hello')</script>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Theory, CombinatorialData, WorkItem("https://github.com/dotnet/aspnetcore/issues/52547")]
    public async Task ScriptTag_WithVariable([CombinatorialValues("7.0", "8.0", "Latest")] string razorLangVersion)
    {
        // Arrange
        var code = """
            @{ var msg = "What's up"; }
            <script>console.log('@msg');</script>
            <div>console.log('@msg');</div>
            <script>console.log('No variable');</script>
            <div>console.log('No variable');</div>
            <script>
                console.log('@msg');
            </script>
            <div>
                console.log('@msg');
            </div>
            <script>
                console.log('No variable');
            </script>
            <div>
                console.log('No variable');
            </div>
            """;
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = $"""
                {code}
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = $"""
                Component:
                {code}
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = razorLangVersion;
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        var suffix = razorLangVersion == "7.0" ? "7" : "8";
        result.VerifyOutputsMatchBaseline(suffix: suffix);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index", suffix: suffix);
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/9051")]
    public async Task LineMapping()
    {
        // Arrange
        var source = """
            <p>The solution to all problems is: @(RaiseHere())</p>
            @code
            {
                private int magicNumber = RaiseHere();
                private static int RaiseHere()
                {
                    return 42;
                }
            }
            """;
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = source,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        result.Diagnostics.Verify();
        result.VerifyOutputsMatchBaseline();

        var original = project.AdditionalDocuments.Single();
        var originalText = await original.GetTextAsync();
        Assert.Equal(source, originalText.ToString());
        var generated = result.GeneratedSources.Single();
        var generatedText = generated.SourceText;
        var generatedTextString = generatedText.ToString();
        var snippet = "RaiseHere()";

        // Find the snippet three times (at line 0, 3, and 4).
        var expectedLines = new[] { 0, 3, 4 };
        var originalIndex = -1;
        var generatedIndex = -1;
        for (var count = 0; ; count++)
        {
            originalIndex = source.IndexOf(snippet, originalIndex + 1, StringComparison.Ordinal);
            generatedIndex = generatedTextString.IndexOf(snippet, generatedIndex + 1, StringComparison.Ordinal);

            if (count == 3)
            {
                Assert.True(originalIndex < 0);
                Assert.True(generatedIndex < 0);
                break;
            }

            var generatedSpan = new TextSpan(generatedIndex, snippet.Length);
            Assert.Equal(snippet, generatedText.ToString(generatedSpan));
            var mapped = generated.SyntaxTree.GetMappedLineSpan(generatedSpan);
            Assert.True(mapped.IsValid);
            Assert.True(mapped.HasMappedPath);
            Assert.Equal("Shared/Component1.razor", mapped.Path);
            var expectedLine = expectedLines[count];
            Assert.Equal(expectedLine, mapped.StartLinePosition.Line);
            Assert.Equal(expectedLine, mapped.EndLinePosition.Line);
            var mappedSpan = originalText.Lines.GetTextSpan(mapped.Span);
            Assert.Equal(snippet, originalText.ToString(mappedSpan));
            Assert.Equal(new TextSpan(originalIndex, snippet.Length), mappedSpan);
        }
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/9050")]
    public async Task LineMapping_Tabs()
    {
        // Arrange
        var tab = '\t';
        var source = $$"""
            <div>
            {{tab}}@if (true)
            {{tab}}{
            {{tab}}{{tab}}@("code")
            {{tab}}}
            </div>
            """;
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = source,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        result.Diagnostics.Verify();
        result.VerifyOutputsMatchBaseline();

        var original = project.AdditionalDocuments.Single();
        var originalText = await original.GetTextAsync();
        Assert.Equal(source, originalText.ToString());
        var generated = result.GeneratedSources.Single();
        var generatedText = generated.SourceText;
        var generatedTextString = generatedText.ToString();

        // Find snippets and verify their mapping.
        var snippets = new[] { "true", "code" };
        var expectedLines = new[] { 1, 3 };
        var originalIndex = -1;
        var generatedIndex = -1;
        foreach (var (snippet, expectedLine) in snippets.Zip(expectedLines))
        {
            originalIndex = source.IndexOf(snippet, originalIndex + 1, StringComparison.Ordinal);
            generatedIndex = generatedTextString.IndexOf(snippet, generatedIndex + 1, StringComparison.Ordinal);
            var generatedSpan = new TextSpan(generatedIndex, snippet.Length);
            Assert.Equal(snippet, generatedText.ToString(generatedSpan));
            var mapped = generated.SyntaxTree.GetMappedLineSpan(generatedSpan);
            Assert.True(mapped.IsValid);
            Assert.True(mapped.HasMappedPath);
            Assert.Equal("Shared/Component1.razor", mapped.Path);
            Assert.Equal(expectedLine, mapped.StartLinePosition.Line);
            Assert.Equal(expectedLine, mapped.EndLinePosition.Line);
            var mappedSpan = originalText.Lines.GetTextSpan(mapped.Span);
            Assert.Equal(snippet, originalText.ToString(mappedSpan));
            Assert.Equal(new TextSpan(originalIndex, snippet.Length), mappedSpan);
        }
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/10180")]
    public async Task TextAfterCodeBlockInMarkupTransition()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                @{
                    @:@{ <i>x y z </i> }
                    <text>a b c</text>
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        Assert.Equal(2, result.GeneratedSources.Length);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/9381")]
    public async Task UnrecognizedComponentName()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Shared/Component1.razor"] = """
                <X1 />
                <X2 @key="null" />
                <X3 @ref="x" />
                <X4 @bind="x" />
                <X5 @bind-Value="x" @bind-Value:event="oninput" />
                <X6 @formname="n" />
                <X7 @rendermode="null" />

                @code {
                    object? x;
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver);

        // Assert
        result.Diagnostics.Verify(
            Diagnostic("RZ10012").WithLocation(1, 1),
            Diagnostic("RZ10012").WithLocation(2, 1),
            Diagnostic("RZ10012").WithLocation(3, 1),
            Diagnostic("RZ10012").WithLocation(4, 1),
            Diagnostic("RZ10012").WithLocation(5, 1),
            Diagnostic("RZ10012").WithLocation(6, 1),
            Diagnostic("RZ10022").WithLocation(6, 16), // Attribute '@formname' can only be applied to 'form' elements.
            Diagnostic("RZ10012").WithLocation(7, 1),
            Diagnostic("RZ10023").WithLocation(7, 18)); // Attribute '@rendermode' is only valid when used on a component.
        Assert.Single(result.GeneratedSources);
    }

    [Theory, CombinatorialData, WorkItem("https://github.com/dotnet/aspnetcore/issues/48778")]
    public async Task ImplicitStringConversion_ParameterCasing(
        [CombinatorialValues("StringParameter", "stringParameter")] string paramName,
        [CombinatorialValues("7.0", "8.0", "Latest")] string langVersion)
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = $$"""
                @{ var c = new MyClass(); }
                <MyComponent {{paramName}}="@c" />
                """,
            ["Shared/MyComponent.razor"] = """
                MyComponent: @StringParameter
                @code {
                    [Parameter]
                    public string StringParameter { get; set; } = "";
                }
                """,
        }, new()
        {
            ["Shared/MyClass.cs"] = """
                namespace MyApp.Shared;
                public class MyClass
                {
                    public static implicit operator string(MyClass c) => "c converted to string";
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = langVersion;
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Theory, CombinatorialData, WorkItem("https://github.com/dotnet/aspnetcore/issues/48778")]
    public async Task ImplicitStringConversion_ParameterCasing_Bind(
        [CombinatorialValues("StringParameter", "stringParameter")] string paramName,
        [CombinatorialValues("7.0", "8.0", "Latest")] string langVersion)
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = $$"""
                @{ var s = "abc"; }
                <MyComponent {{paramName}}="@s" />
                """,
            ["Shared/MyComponent.razor"] = """
                MyComponent: @StringParameter
                @code {
                    [Parameter] public string StringParameter { get; set; } = "";
                    [Parameter] public EventCallback<string> StringParameterChanged { get; set; }
                }
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project, options =>
        {
            options.TestGlobalOptions["build_property.RazorLangVersion"] = langVersion;
        });

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        Assert.Empty(result.Diagnostics);
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }

    [Fact, WorkItem("https://github.com/dotnet/razor/issues/11327")]
    public async Task QuoteInAttributeName()
    {
        // Arrange
        var project = CreateTestProject(new()
        {
            ["Views/Home/Index.cshtml"] = """
                @(await Html.RenderComponentAsync<MyApp.Shared.Component1>(RenderMode.Static))
                """,
            ["Shared/Component1.razor"] = """
                <div class="test""></div>
                """,
        });
        var compilation = await project.GetCompilationAsync();
        var driver = await GetDriverAsync(project);

        // Act
        var result = RunGenerator(compilation!, ref driver, out compilation);

        // Assert
        result.Diagnostics.Verify();
        await VerifyRazorPageMatchesBaselineAsync(compilation, "Views_Home_Index");
    }
}
