﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CommentSelection;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Roslyn.Test.EditorUtilities;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.CommentSelection;

[UseExportProvider]
[Trait(Traits.Feature, Traits.Features.CommentSelection)]
public sealed class CommentUncommentSelectionCommandHandlerTests
{
    private sealed class MockCommentSelectionService : AbstractCommentSelectionService
    {
        public MockCommentSelectionService(bool supportsBlockComment)
            => SupportsBlockComment = supportsBlockComment;

        public override string SingleLineCommentString => "//";
        public override bool SupportsBlockComment { get; }

        public override string BlockCommentStartString
        {
            get
            {
                if (!this.SupportsBlockComment)
                {
                    throw new NotSupportedException();
                }

                return "/*";
            }
        }

        public override string BlockCommentEndString
        {
            get
            {
                if (!this.SupportsBlockComment)
                {
                    throw new NotSupportedException();
                }

                return "*/";
            }
        }
    }

    [Fact]
    public void Create()
    {
        Assert.NotNull(
            new MockCommentSelectionService(
                supportsBlockComment: true));
    }

    [WpfFact]
    public void Comment_EmptyLine()
    {
        var code = @"|start||end|";
        CommentSelection(code, [], supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_NoSelectionAtEndOfLine()
    {
        var code = @"Some text on a line|start||end|";
        CommentSelection(code, new[] { new TextChange(TextSpan.FromBounds(0, 0), "//") }, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_Whitespace()
    {
        var code = @"  |start|   |end|   ";
        CommentSelection(code, [], supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_SingleLineBlockWithBlockSelection()
    {
        var code = @"this is |start| some |end| text";
        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(8, 0), "/*"),
            new TextChange(new TextSpan(14, 0), "*/"),
        };
        CommentSelection(code, expectedChanges, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_MultilineWithBlockSelection()
    {
        var code = @"this is |start| some 
text that is |end| on
multiple lines";
        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(0, 0), "//"),
            new TextChange(new TextSpan(16, 0), "//"),
        };
        CommentSelection(code, expectedChanges, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_SingleLineBlockWithNoBlockSelection()
    {
        var code = @"this is |start| some |end| text";
        CommentSelection(code, new[] { new TextChange(TextSpan.FromBounds(0, 0), "//") }, supportBlockComments: false);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/563915")]
    [WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530300")]
    public void Comment_MultilineIndented()
    {
        var code = @"
class Goo
{
    |start|void M()
    {
    }|end|
}
";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "//"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "//"),
        };
        CommentSelection(
            code,
            expectedChanges,
            supportBlockComments: false,
            expectedSelectedSpans: new[] { Span.FromBounds(16, 48) });
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/527190")]
    [WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/563924")]
    public void Comment_ApplyTwice()
    {
        var code = @"|start|class C
{
    void M() { }
}|end|
";
        var exportProvider = CreateExportProvider();
        using var disposableView = EditorFactory.CreateView(exportProvider, code);
        var selectedSpans = SetupSelection(disposableView.TextView);

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(0, 0), "//"),
            new TextChange(new TextSpan(9, 0), "//"),
            new TextChange(new TextSpan(12, 0), "//"),
            new TextChange(new TextSpan(30, 0), "//"),
        };
        CommentSelection(
            exportProvider,
            disposableView.TextView,
            expectedChanges,
            supportBlockComments: false,
            expectedSelectedSpans: new[] { new Span(0, 39) });

        expectedChanges =
        [
            new TextChange(new TextSpan(0, 0), "//"),
            new TextChange(new TextSpan(11, 0), "//"),
            new TextChange(new TextSpan(16, 0), "//"),
            new TextChange(new TextSpan(36, 0), "//"),
        ];
        CommentSelection(
            exportProvider,
            disposableView.TextView,
            expectedChanges,
            supportBlockComments: false,
            expectedSelectedSpans: new[] { new Span(0, 47) });
    }

    [WpfFact]
    public void Comment_SelectionEndsAtColumnZero()
    {
        var code = @"
class Goo
{
|start|    void M()
    {
    }
|end|}
";
        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "//"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "//"),
        };
        CommentSelection(code, expectedChanges, supportBlockComments: false);
    }

    [WpfFact]
    public void Comment_BoxSelectionAtStartOfLines()
    {
        var code = @"
class Goo
{
|start||end|    void M()
|start||end|    {
|start||end|    }
}
";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "//"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "//"),
        };

        CommentSelection(code, expectedChanges, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_BoxSelectionIndentedAtStart()
    {
        var code = @"
class Goo
{
    |start||end|void M()
    |start||end|{
    |start||end|}
}
";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "//"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "//"),
        };

        CommentSelection(code, expectedChanges, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_BoxSelectionBlock()
    {
        var code = @"
class Goo
{
    |start|v|end|oid M()
    |start|{|end|
    |start|o|end|ther
    |start|}|end|
}
";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "/*"),
            new TextChange(new TextSpan(21, 0), "*/"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "/*"),
            new TextChange(new TextSpan(42, 0), "*/"),
            new TextChange(new TextSpan(52, 0), "//"),
        };

        CommentSelection(code, expectedChanges, supportBlockComments: true);
    }

    [WpfFact]
    public void Comment_BoxSelectionBlockWithoutSupport()
    {
        var code = @"
class Goo
{
    |start|v|end|oid M()
    |start|{|end|
    |start|}|end|
}
";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 0), "//"),
            new TextChange(new TextSpan(34, 0), "//"),
            new TextChange(new TextSpan(41, 0), "//"),
        };
        CommentSelection(code, expectedChanges, supportBlockComments: false);
    }

    [WpfFact]
    public void Uncomment_NoSelection()
    {
        var code = @"//Goo|start||end|Bar";
        UncommentSelection(code, new[] { new TextChange(new TextSpan(0, 2), string.Empty) }, Span.FromBounds(0, 6), supportBlockComments: true);
    }

    [WpfFact]
    public void Uncomment_MatchesBlockComment()
    {
        var code = @"Before |start|/* Some Commented Text */|end| after";
        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(7, 2), string.Empty),
            new TextChange(new TextSpan(30, 2), string.Empty),
        };

        UncommentSelection(code, expectedChanges, Span.FromBounds(7, 28), supportBlockComments: true);
    }

    [WpfFact]
    public void Uncomment_InWhitespaceOutsideBlockComment()
    {
        var code = @"Before |start|    /* Some Commented Text */    |end| after";
        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(11, 2), string.Empty),
            new TextChange(new TextSpan(34, 2), string.Empty),
        };

        UncommentSelection(code, expectedChanges, Span.FromBounds(11, 32), supportBlockComments: true);
    }

    [WpfFact]
    public void Uncomment_IndentedSingleLineCommentsAndUncommentedLines()
    {
        var code = @"
class C
{
|start|    //void M()
    //{
        //if (true)
        //{
            SomethingNotCommented();
        //}
    //}
|end|}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(18, 2), string.Empty),
            new TextChange(new TextSpan(34, 2), string.Empty),
            new TextChange(new TextSpan(47, 2), string.Empty),
            new TextChange(new TextSpan(68, 2), string.Empty),
            new TextChange(new TextSpan(119, 2), string.Empty),
            new TextChange(new TextSpan(128, 2), string.Empty),
        };

        UncommentSelection(code, expectedChanges, Span.FromBounds(14, 119), supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/563927")]
    // This test is just measuring current behavior, there is no reason not to support maintaining box selection.
    public void Uncomment_BoxSelection()
    {
        var code = @"
class Goo
{
    |start|/*v*/|end|oid M()
    |start|//{  |end|
    |start|/*o*/|end|ther
    |start|//}  |end|
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 2), string.Empty),
            new TextChange(new TextSpan(23, 2), string.Empty),
            new TextChange(new TextSpan(38, 2), string.Empty),
            new TextChange(new TextSpan(49, 2), string.Empty),
            new TextChange(new TextSpan(52, 2), string.Empty),
            new TextChange(new TextSpan(64, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
            {
                Span.FromBounds(20, 21)
             };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact]
    public void Uncomment_PartOfMultipleComments()
    {
        var code = @"
//|start|//namespace N
////{
//|end|//}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(2, 2), string.Empty),
            new TextChange(new TextSpan(19, 2), string.Empty),
            new TextChange(new TextSpan(26, 2), string.Empty),
        };
        UncommentSelection(code, expectedChanges, Span.FromBounds(2, 25), supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530300")]
    [WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/563924")]
    public void Comment_NoSelectionAtStartOfLine()
    {
        var code = @"|start||end|using System;";
        CommentSelection(code, new[] { new TextChange(TextSpan.FromBounds(0, 0), "//") }, new[] { new Span(0, 15) }, supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_NoSelectionInBlockComment()
    {
        var code = @"using /* Sy|start||end|stem.*/IO;";
        UncommentSelection(code,
            expectedChanges: new[]
            {
                new TextChange(new TextSpan(6, 2), string.Empty),
                new TextChange(new TextSpan(16, 2), string.Empty)
            },
            expectedSelectedSpan: new Span(6, 8),
            supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_BlockCommentWithPreviousBlockComment()
    {
        var code = @"/* comment */using /* Sy|start||end|stem.*/IO;";
        UncommentSelection(code,
            expectedChanges: new[]
            {
                new TextChange(new TextSpan(19, 2), string.Empty),
                new TextChange(new TextSpan(29, 2), string.Empty)
            },
            expectedSelectedSpan: new Span(19, 8),
            supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_InsideEndOfBlockComment()
    {
        var code = @"/*using System;*|start||end|/";
        UncommentSelection(code,
            expectedChanges: new[]
            {
                new TextChange(new TextSpan(0, 2), string.Empty),
                new TextChange(new TextSpan(15, 2), string.Empty)
            },
            expectedSelectedSpan: new Span(0, 13),
            supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_AtBeginningOfEndOfBlockComment()
    {
        var code = @"/*using System;|start||end|*/";
        UncommentSelection(code,
            expectedChanges: new[]
            {
                new TextChange(new TextSpan(0, 2), string.Empty),
                new TextChange(new TextSpan(15, 2), string.Empty)
            },
            expectedSelectedSpan: new Span(0, 13),
            supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_AtEndOfBlockComment()
    {
        var code = @"/*using System;*/|start||end|";
        UncommentSelection(code, [], new Span(17, 0), supportBlockComments: true);
    }

    [WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/932411")]
    public void Uncomment_BlockCommentWithNoEnd()
    {
        var code = @"/*using |start||end|System;";
        UncommentSelection(code, [], new Span(8, 0), supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_BlockWithSingleInside()
    {
        var code = @"
class A
{
    |start|/*
    void M()
    {
            // A comment
            // Another comment
    }
    */|end|
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(18, 2), string.Empty),
            new TextChange(new TextSpan(112, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(18, 110)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_BlockWithSingleInsideAndSelectionIncludesNewLines()
    {
        var code = @"
class A
{
|start|
    /*
    void M()
    {
            // A comment
            // Another comment
    }
    */
|end|
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(20, 2), string.Empty),
            new TextChange(new TextSpan(114, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(20, 112)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_BlockWithSingleInsideAndSelectionStartsWithSpaces()
    {
        var code = @"
class A
{
|start|    /*
    void M()
    {
            // A comment
            // Another comment
    }
    */
|end|}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(18, 2), string.Empty),
            new TextChange(new TextSpan(112, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(18, 110)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_BlockWithSingleInsideAndBlockSelected()
    {
        var code = @"
class A
{
    /*
    void |start|M|end|()
    {
            // A comment
            // Another comment
    }
    */
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(18, 2), string.Empty),
            new TextChange(new TextSpan(112, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(18, 110)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_SingleLineInsideBlockAndSingleSelected()
    {
        var code = @"
class A
{
    /*
    void M()
    {
            // A |start|comm|end|ent
            // Another comment
    }
    */
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(55, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(43, 65)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_SingleLineInsideBlockAndBothSelected()
    {
        var code = @"
class A
{
    /*
    void |start|M()
    {
            // A comm|end|ent
            // Another comment
    }
    */
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(55, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(22, 65)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    [WpfFact, WorkItem("https://github.com/dotnet/roslyn/issues/31669")]
    public void Uncomment_SingleLinesWithBlockAndSingleInside()
    {
        var code = @"
class A
{
    |start|///*
    //void M()
    //{
    //     // A comment
    //     // Another comment
    //}
    //*/|end|
}";

        var expectedChanges = new[]
        {
            new TextChange(new TextSpan(18, 2), string.Empty),
            new TextChange(new TextSpan(28, 2), string.Empty),
            new TextChange(new TextSpan(44, 2), string.Empty),
            new TextChange(new TextSpan(53, 2), string.Empty),
            new TextChange(new TextSpan(78, 2), string.Empty),
            new TextChange(new TextSpan(109, 2), string.Empty),
            new TextChange(new TextSpan(118, 2), string.Empty),
        };

        var expectedSelectedSpans = new[]
        {
            Span.FromBounds(14, 108)
        };

        UncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments: true);
    }

    private static void UncommentSelection(string code, IEnumerable<TextChange> expectedChanges, Span expectedSelectedSpan, bool supportBlockComments)
        => CommentOrUncommentSelection(code, expectedChanges, new[] { expectedSelectedSpan }, supportBlockComments, Operation.Uncomment);

    private static void UncommentSelection(string code, IEnumerable<TextChange> expectedChanges, IEnumerable<Span> expectedSelectedSpans, bool supportBlockComments)
        => CommentOrUncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments, Operation.Uncomment);

    private static void CommentSelection(string code, IEnumerable<TextChange> expectedChanges, bool supportBlockComments)
        => CommentOrUncommentSelection(code, expectedChanges, null /*expectedSelectedSpans*/, supportBlockComments, Operation.Comment);

    private static void CommentSelection(string code, IEnumerable<TextChange> expectedChanges, IEnumerable<Span> expectedSelectedSpans, bool supportBlockComments)
        => CommentOrUncommentSelection(code, expectedChanges, expectedSelectedSpans, supportBlockComments, Operation.Comment);

    private static void CommentSelection(ExportProvider exportProvider, ITextView textView, IEnumerable<TextChange> expectedChanges, IEnumerable<Span> expectedSelectedSpans, bool supportBlockComments)
        => CommentOrUncommentSelection(exportProvider, textView, expectedChanges, expectedSelectedSpans, supportBlockComments, Operation.Comment);

    private static ExportProvider CreateExportProvider()
        => EditorTestCompositions.EditorFeatures.ExportProviderFactory.CreateExportProvider();

    private static void CommentOrUncommentSelection(
        string code,
        IEnumerable<TextChange> expectedChanges,
        IEnumerable<Span> expectedSelectedSpans,
        bool supportBlockComments,
        Operation operation)
    {
        var exportProvider = CreateExportProvider();

        using var disposableView = EditorFactory.CreateView(exportProvider, code);
        var selectedSpans = SetupSelection(disposableView.TextView);

        CommentOrUncommentSelection(exportProvider, disposableView.TextView, expectedChanges, expectedSelectedSpans, supportBlockComments, operation);
    }

    private static void CommentOrUncommentSelection(
        ExportProvider exportProvider,
        ITextView textView,
        IEnumerable<TextChange> expectedChanges,
        IEnumerable<Span> expectedSelectedSpans,
        bool supportBlockComments,
        Operation operation)
    {
        var textUndoHistoryRegistry = exportProvider.GetExportedValue<ITextUndoHistoryRegistry>();
        var editorOperationsFactory = exportProvider.GetExportedValue<IEditorOperationsFactoryService>();
        var editorOptionsService = exportProvider.GetExportedValue<EditorOptionsService>();
        var commandHandler = new CommentUncommentSelectionCommandHandler(textUndoHistoryRegistry, editorOperationsFactory, editorOptionsService);
        var service = new MockCommentSelectionService(supportBlockComments);

        var edits = commandHandler.CollectEdits(
            null, service, textView.TextBuffer, textView.Selection.GetSnapshotSpansOnBuffer(textView.TextBuffer), operation, CancellationToken.None);

        AssertEx.SetEqual(expectedChanges, edits.TextChanges);

        var trackingSpans = edits.TrackingSpans
            .Select(textSpan => AbstractCommentSelectionBase<Operation>.CreateTrackingSpan(
                edits.ResultOperation, textView.TextBuffer.CurrentSnapshot, textSpan.TrackingTextSpan))
            .ToList();

        // Actually apply the edit to let the tracking spans adjust.
        using (var edit = textView.TextBuffer.CreateEdit())
        {
            edits.TextChanges.Do(tc => edit.Replace(tc.Span.ToSpan(), tc.NewText));

            edit.Apply();
        }

        if (trackingSpans.Any())
        {
            textView.SetSelection(trackingSpans.First().GetSpan(textView.TextBuffer.CurrentSnapshot));
        }

        if (expectedSelectedSpans != null)
        {
            AssertEx.Equal(expectedSelectedSpans, textView.Selection.SelectedSpans.Select(snapshotSpan => snapshotSpan.Span));
        }
    }

    private static IEnumerable<Span> SetupSelection(IWpfTextView textView)
    {
        var spans = new List<Span>();
        while (true)
        {
            var startOfSelection = FindAndRemoveMarker(textView, "|start|");
            var endOfSelection = FindAndRemoveMarker(textView, "|end|");

            if (startOfSelection < 0)
            {
                break;
            }
            else
            {
                spans.Add(Span.FromBounds(startOfSelection, endOfSelection));
            }
        }

        var snapshot = textView.TextSnapshot;
        if (spans.Count == 1)
        {
            textView.Selection.Select(new SnapshotSpan(snapshot, spans.Single()), isReversed: false);
            textView.Caret.MoveTo(new SnapshotPoint(snapshot, spans.Single().End));
        }
        else
        {
            textView.Selection.Mode = TextSelectionMode.Box;
            textView.Selection.Select(new VirtualSnapshotPoint(snapshot, spans.First().Start),
                                      new VirtualSnapshotPoint(snapshot, spans.Last().End));
            textView.Caret.MoveTo(new SnapshotPoint(snapshot, spans.Last().End));
        }

        return spans;
    }

    private static int FindAndRemoveMarker(ITextView textView, string marker)
    {
        var index = textView.TextSnapshot.GetText().IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            textView.TextBuffer.Delete(new Span(index, marker.Length));
        }

        return index;
    }
}
