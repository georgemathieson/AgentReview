using AgentReview.Core.Diff;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgentReview.Core.Regions;

/// <summary>
/// Uses Roslyn to expand every changed line to the complete member that contains it (method, constructor,
/// property, field, event, enum, delegate...), including its doc comments and attributes. Lines removed
/// from the base version are located in the base syntax tree, added lines in the head syntax tree.
/// </summary>
public sealed class CSharpStrategy : IRegionStrategy
{
    public bool CanHandle(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<Region> GetRegions(DiffDocument doc, ReviewOptions options)
    {
        var newSide = new Lazy<Side>(() => new Side(doc.NewText, isNew: true));
        var oldSide = new Lazy<Side>(() => new Side(doc.OldText, isNew: false));
        var emitted = new HashSet<(bool, TextSpan)>();

        foreach (var group in doc.ChangeGroups)
        {
            for (var i = group.Start; i <= group.End; i++)
            {
                var row = doc.Rows[i];
                var (side, line) = row.Kind == RowKind.Added ? (newSide.Value, row.NewLine!.Value) : (oldSide.Value, row.OldLine!.Value);
                var member = side.FindMember(line);

                if (member is null)
                {
                    yield return RegionHelpers.ContextAround(doc, i, i, options.ContextLines);
                    continue;
                }
                if (!emitted.Add((side.IsNew, member.Span)))
                    continue;

                foreach (var region in RegionsForMember(doc, side, member, line, i, options))
                    yield return region;
            }
        }
    }

    private static IEnumerable<Region> RegionsForMember(DiffDocument doc, Side side, MemberDeclarationSyntax member, int changedLine, int changedRow, ReviewOptions options)
    {
        var (first, last) = side.LineRange(member);
        var description = Describe(member);

        if (last - first + 1 <= options.MaxUnitLines)
        {
            var (s, e) = side.ToRows(doc, first, last);
            yield return new Region(s, e, $"complete {description}");
            yield break;
        }

        // Too large to show whole: show its declaration (doc comments, attributes, signature) plus the changed area.
        var headerEnd = side.HeaderEndLine(member, first);
        var (hs, he) = side.ToRows(doc, first, Math.Min(headerEnd, first + 40));
        var note = member is TypeDeclarationSyntax
            ? $"The type is {last - first + 1} lines long, so only its declaration and the changed area are shown; other members are unchanged and omitted."
            : $"This member is {last - first + 1} lines long (limit {options.MaxUnitLines}), so only its declaration and the changed area are shown.";
        yield return new Region(hs, he, $"declaration of {description}", note);
        var context = RegionHelpers.ContextAround(doc, changedRow, changedRow, Math.Max(options.ContextLines, 15));
        yield return context with { Description = $"changed area inside {description}" };
    }

    private sealed class Side(string text, bool isNew)
    {
        private readonly SourceText _text = SourceText.From(text);
        private SyntaxNode? _root;

        public bool IsNew { get; } = isNew;

        private SyntaxNode Root => _root ??= CSharpSyntaxTree.ParseText(_text, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();

        public (int Start, int End) ToRows(DiffDocument doc, int first, int last) =>
            IsNew ? doc.RowsForNewLines(first, last) : doc.RowsForOldLines(first, last);

        public MemberDeclarationSyntax? FindMember(int line)
        {
            if (_text.Length == 0 || line < 1 || line > _text.Lines.Count)
                return null;

            var span = _text.Lines[line - 1].Span;
            var position = span.Start;
            while (position < span.End && char.IsWhiteSpace(_text[position]))
                position++;
            position = Math.Min(position, _text.Length - 1);

            var token = Root.FindToken(position);
            var member = token.Parent?.AncestorsAndSelf()
                .OfType<MemberDeclarationSyntax>()
                .FirstOrDefault(m => m is not BaseNamespaceDeclarationSyntax);

            // An enum value is best understood with the whole enum.
            if (member is EnumMemberDeclarationSyntax { Parent: MemberDeclarationSyntax parentEnum })
                member = parentEnum;
            return member;
        }

        /// <summary>1-based line range of a member, including leading doc comments and attributes but not blank lines.</summary>
        public (int First, int Last) LineRange(SyntaxNode node)
        {
            var start = node.SpanStart;
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    start = trivia.SpanStart;
                    break;
                }
            }
            return (_text.Lines.GetLineFromPosition(start).LineNumber + 1, _text.Lines.GetLineFromPosition(node.Span.End).LineNumber + 1);
        }

        /// <summary>Last line of a member's declaration: the line holding its opening brace or expression-body arrow.</summary>
        public int HeaderEndLine(SyntaxNode node, int firstLine)
        {
            var token = node.DescendantTokens().FirstOrDefault(t =>
                t.IsKind(SyntaxKind.OpenBraceToken) || t.IsKind(SyntaxKind.EqualsGreaterThanToken));
            return token.IsKind(SyntaxKind.None) ? firstLine : _text.Lines.GetLineFromPosition(token.SpanStart).LineNumber + 1;
        }
    }

    internal static string Describe(MemberDeclarationSyntax member)
    {
        var (kind, name) = member switch
        {
            MethodDeclarationSyntax m => ("method", $"{m.Identifier.Text}{m.TypeParameterList}{Parameters(m.ParameterList)}"),
            ConstructorDeclarationSyntax c => ("constructor", $"{c.Identifier.Text}{Parameters(c.ParameterList)}"),
            DestructorDeclarationSyntax d => ("finalizer", $"~{d.Identifier.Text}()"),
            OperatorDeclarationSyntax o => ("operator", $"operator {o.OperatorToken.Text}{Parameters(o.ParameterList)}"),
            ConversionOperatorDeclarationSyntax co => ("conversion operator", $"{co.ImplicitOrExplicitKeyword.Text} operator {co.Type}"),
            PropertyDeclarationSyntax p => ("property", p.Identifier.Text),
            IndexerDeclarationSyntax ix => ("indexer", $"this[{string.Join(", ", ix.ParameterList.Parameters.Select(ParamType))}]"),
            EventDeclarationSyntax e => ("event", e.Identifier.Text),
            EventFieldDeclarationSyntax ef => ("event", Variables(ef.Declaration)),
            FieldDeclarationSyntax f => (f.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field", Variables(f.Declaration)),
            DelegateDeclarationSyntax dl => ("delegate", dl.Identifier.Text),
            EnumDeclarationSyntax en => ("enum", en.Identifier.Text),
            RecordDeclarationSyntax r => (r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record", r.Identifier.Text),
            TypeDeclarationSyntax t => (t.Keyword.Text, t.Identifier.Text),
            GlobalStatementSyntax => ("top-level statement", ""),
            _ => ("member", ""),
        };

        var container = string.Join('.', member.Ancestors().Select(a => a switch
        {
            BaseTypeDeclarationSyntax t => t.Identifier.Text,
            BaseNamespaceDeclarationSyntax n => n.Name.ToString(),
            _ => null,
        }).Where(n => n is not null).Reverse());

        var text = name.Length > 0 ? $"{kind} `{name}`" : kind;
        return container.Length > 0 ? $"{text} in `{container}`" : text;
    }

    private static string Parameters(ParameterListSyntax list) => $"({string.Join(", ", list.Parameters.Select(ParamType))})";

    private static string ParamType(ParameterSyntax p) => p.Type?.ToString() ?? p.Identifier.Text;

    private static string Variables(VariableDeclarationSyntax d) => string.Join(", ", d.Variables.Select(v => v.Identifier.Text));
}
