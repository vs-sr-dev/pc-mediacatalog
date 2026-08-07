using System.Globalization;

namespace MediaCatalog.Core.Consolidation;

/// <summary>
/// Reads the little comparison language into something that can be run.
///
/// The language is deliberately tiny — a list of <c>if (condition) action</c> lines and
/// nothing else — because every feature it does not have is a way the rules cannot go wrong.
/// There are no variables, no loops and no way to name a file other than as one of the two
/// being compared, so the worst a script can do is decline to choose.
///
/// Everything it will accept is listed in <see cref="RuleScriptVocabulary"/>, and a name that
/// is not on that list is refused with the line it was on rather than quietly ignored.
/// </summary>
public static class RuleScriptParser
{
    private enum TokenKind { Identifier, Number, Symbol, End }

    private record Token(TokenKind Kind, string Text, int Line, int Start, int Length)
    {
        public bool Is(string text) =>
            string.Equals(Text, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parse, or throw a <see cref="RuleScriptException"/> saying where it went wrong.</summary>
    public static RuleScriptProgram Parse(string source)
    {
        source ??= string.Empty;
        var tokens = Tokenise(source);
        var state = new ParseState(tokens, source);

        var statements = new List<ScriptStatement>();
        while (!state.AtEnd) statements.Add(state.ReadStatement());

        return new RuleScriptProgram(statements)
        {
            NeedsProbe = statements.Any(s => Mentions(s, MentionsLength)),
            NeedsDeepScan = statements.Any(s => Mentions(s, MentionsDecode)),
            NeedsFingerprint = statements.Any(s => Mentions(s, MentionsFingerprint))
        };
    }

    /// <summary>Parse without throwing, for the places that want to show the problem rather than raise it.</summary>
    public static bool TryParse(string source, out RuleScriptProgram program, out string error)
    {
        try
        {
            program = Parse(source);
            error = string.Empty;
            return true;
        }
        catch (RuleScriptException ex)
        {
            program = new RuleScriptProgram(Array.Empty<ScriptStatement>());
            error = ex.ToString();
            return false;
        }
    }

    /// <summary>The script written back out, one statement to a line, spelt the tidy way.</summary>
    public static string Format(RuleScriptProgram program) =>
        string.Join(Environment.NewLine, program.Statements.Select(s => s.Text));

    /// <summary>One statement as its own line of source.</summary>
    public static string Describe(ScriptStatement statement) => statement.Text;

    // --- Back the other way, for the builder --------------------------------

    /// <summary>
    /// A condition broken back into the pieces it was built out of, so a script somebody
    /// typed — or wrote here last week — can be picked up and rearranged by hand rather than
    /// only read. Every piece is something the palette can offer, which is what keeps the two
    /// ways of writing a rule the same rule.
    /// </summary>
    public static List<string> Pieces(ScriptExpr? expr)
    {
        var pieces = new List<string>();
        if (expr != null) Write(expr, pieces, parent: null);
        return pieces;
    }

    /// <summary>An action as the one piece it is: "Consolidate(File1)", "Undecided".</summary>
    public static string Written(ScriptAction action) => action switch
    {
        ConsolidateAction c => $"Consolidate({c.Side})",
        UndecidedAction => "Undecided",
        CallAction call => Call(call.Call),
        _ => string.Empty
    };

    private static string Call(CallExpr call) =>
        $"{call.Name}({string.Join(", ", call.Arguments.Select(Argument))})";

    private static string Argument(ScriptExpr arg) => arg switch
    {
        FileExpr f => f.Side.ToString(),
        NumberExpr n => n.Value.ToString("0.##", CultureInfo.InvariantCulture),
        TruthExpr t => t.Value ? "true" : "false",
        PropertyExpr p => $"{p.Side}.{p.Property}",
        _ => string.Empty
    };

    private static void Write(ScriptExpr expr, List<string> pieces, ScriptExpr? parent)
    {
        switch (expr)
        {
            case PropertyExpr p:
                pieces.Add($"{p.Side}.{p.Property}");
                return;
            case FileExpr f:
                pieces.Add(f.Side.ToString());
                return;
            case NumberExpr n:
                pieces.Add(n.Value.ToString("0.##", CultureInfo.InvariantCulture));
                return;
            case TruthExpr t:
                pieces.Add(t.Value ? "true" : "false");
                return;
            case CallExpr call:
                pieces.Add(Call(call));
                return;
            case NotExpr not:
                pieces.Add("NOT");
                Write(not.Inner, pieces, not);
                return;
            case CompareExpr compare:
                Write(compare.Left, pieces, compare);
                pieces.Add(compare.Operator);
                Write(compare.Right, pieces, compare);
                return;
            case LogicExpr logic:
            {
                // Brackets only where dropping them would change what the rule says: inside a
                // NOT, or where an OR sits under an AND.
                var bracket = parent is NotExpr ||
                              (parent is LogicExpr outer && outer.And && !logic.And);
                if (bracket) pieces.Add("(");
                Write(logic.Left, pieces, logic);
                pieces.Add(logic.And ? "AND" : "OR");
                Write(logic.Right, pieces, logic);
                if (bracket) pieces.Add(")");
                return;
            }
        }
    }

    // --- What a script asks the run to measure ------------------------------

    private static bool Mentions(ScriptStatement statement, Func<ScriptExpr, bool> predicate) =>
        (statement.Condition != null && Walk(statement.Condition, predicate)) ||
        statement.Action switch
        {
            CallAction call => Walk(call.Call, predicate),
            _ => false
        };

    private static bool Walk(ScriptExpr expr, Func<ScriptExpr, bool> predicate) =>
        predicate(expr) || expr switch
        {
            CompareExpr c => Walk(c.Left, predicate) || Walk(c.Right, predicate),
            LogicExpr l => Walk(l.Left, predicate) || Walk(l.Right, predicate),
            NotExpr n => Walk(n.Inner, predicate),
            CallExpr call => call.Arguments.Any(a => Walk(a, predicate)),
            _ => false
        };

    private static bool MentionsLength(ScriptExpr expr) => expr switch
    {
        PropertyExpr p => p.Property is "Length" or "Quality",
        CallExpr c => c.Name == "LengthDifferent",
        _ => false
    };

    private static bool MentionsDecode(ScriptExpr expr) => expr switch
    {
        PropertyExpr p => p.Property is "DeepCheckIntegrity" or "Corrupt" or "Checked",
        CallExpr c => c.Name == "DeepScan",
        _ => false
    };

    private static bool MentionsFingerprint(ScriptExpr expr) => expr switch
    {
        PropertyExpr p => p.Property is "HasFingerprint",
        CallExpr c => c.Name is "FingerprintFiles" or "FingerprintsMatch",
        _ => false
    };

    // --- Tokens -------------------------------------------------------------

    private static readonly string[] Operators =
        { ">=", "<=", "==", "!=", "<>", "&&", "||", ">", "<", "=", "(", ")", ",", ".", "!" };

    private static List<Token> Tokenise(string source)
    {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }

            // A comment runs to the end of its line: somebody who has built a page of rules
            // will want to say why one of them is there.
            if (c == '#' || (c == '/' && i + 1 < source.Length && source[i + 1] == '/'))
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Identifier, source[start..i], line, start, i - start));
                continue;
            }

            if (char.IsDigit(c))
            {
                var start = i;
                while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
                tokens.Add(new Token(TokenKind.Number, source[start..i], line, start, i - start));
                continue;
            }

            var symbol = Operators.FirstOrDefault(op =>
                i + op.Length <= source.Length && source.AsSpan(i, op.Length).SequenceEqual(op));
            if (symbol == null)
                throw new RuleScriptException($"'{c}' is not something a rule can contain.", line);

            tokens.Add(new Token(TokenKind.Symbol, symbol, line, i, symbol.Length));
            i += symbol.Length;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, line, source.Length, 0));
        return tokens;
    }

    // --- Statements and expressions -----------------------------------------

    private sealed class ParseState
    {
        private readonly List<Token> _tokens;
        private readonly string _source;
        private int _at;

        public ParseState(List<Token> tokens, string source)
        {
            _tokens = tokens;
            _source = source;
        }

        public bool AtEnd => Current.Kind == TokenKind.End;

        private Token Current => _tokens[_at];

        private Token Take() => _tokens[_at++];

        private bool TakeIf(string text)
        {
            if (!Current.Is(text) || Current.Kind == TokenKind.End) return false;
            _at++;
            return true;
        }

        private void Expect(string text)
        {
            if (!TakeIf(text))
                throw new RuleScriptException(
                    $"'{text}' was expected here, and '{Describe(Current)}' is what is there.",
                    Current.Line);
        }

        private static string Describe(Token token) =>
            token.Kind == TokenKind.End ? "the end of the rules" : token.Text;

        public ScriptStatement ReadStatement()
        {
            var first = Current;
            ScriptExpr? condition = null;

            if (TakeIf("if"))
            {
                Expect("(");
                condition = ReadOr();
                Expect(")");

                // "then" is not required, but somebody will write it, and refusing a script
                // over a word that changes nothing helps nobody.
                TakeIf("then");
            }

            var action = ReadAction();
            var last = _tokens[_at - 1];

            var text = _source[first.Start..(last.Start + last.Length)]
                .Replace("\r", " ").Replace("\n", " ").Trim();
            return new ScriptStatement(condition, action, text);
        }

        private ScriptAction ReadAction()
        {
            if (Current.Kind != TokenKind.Identifier)
                throw new RuleScriptException(
                    $"A rule has to do something — Consolidate, DeepScan, FingerprintFiles or " +
                    $"Undecided — and '{Describe(Current)}' is not one of those.", Current.Line);

            var name = Current.Text;

            if (string.Equals(name, "Undecided", StringComparison.OrdinalIgnoreCase))
            {
                Take();
                if (TakeIf("(")) Expect(")");
                return new UndecidedAction();
            }

            var call = ReadCall();
            if (call.Name == "Consolidate")
                return new ConsolidateAction(((FileExpr)call.Arguments[0]).Side);

            return new CallAction(call);
        }

        // --- Expressions ---

        private ScriptExpr ReadOr()
        {
            var left = ReadAnd();
            while (Current.Is("or") || Current.Is("||"))
            {
                Take();
                left = new LogicExpr(left, false, ReadAnd());
            }
            return left;
        }

        private ScriptExpr ReadAnd()
        {
            var left = ReadNot();
            while (Current.Is("and") || Current.Is("&&"))
            {
                Take();
                left = new LogicExpr(left, true, ReadNot());
            }
            return left;
        }

        private ScriptExpr ReadNot()
        {
            if (Current.Is("not") || Current.Is("!"))
            {
                Take();
                return new NotExpr(ReadNot());
            }
            return ReadComparison();
        }

        private ScriptExpr ReadComparison()
        {
            var left = ReadTerm();

            if (Current.Kind != TokenKind.Symbol) return left;
            var op = Current.Text switch
            {
                ">=" or "<=" or ">" or "<" or "==" or "!=" => Current.Text,
                "=" => "==",
                "<>" => "!=",
                _ => null
            };
            if (op == null) return left;

            Take();
            return new CompareExpr(left, op, ReadTerm());
        }

        private ScriptExpr ReadTerm()
        {
            if (TakeIf("("))
            {
                var inner = ReadOr();
                Expect(")");
                return inner;
            }

            if (Current.Kind == TokenKind.Number)
            {
                var token = Take();
                if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var value))
                    throw new RuleScriptException($"'{token.Text}' is not a number.", token.Line);
                return new NumberExpr(value);
            }

            if (Current.Kind != TokenKind.Identifier)
                throw new RuleScriptException(
                    $"'{Describe(Current)}' cannot be compared with anything.", Current.Line);

            if (Current.Is("true")) { Take(); return new TruthExpr(true); }
            if (Current.Is("false")) { Take(); return new TruthExpr(false); }

            if (SideOf(Current.Text) is { } side)
            {
                var file = Take();
                if (!TakeIf("."))
                    return new FileExpr(side);

                if (Current.Kind != TokenKind.Identifier)
                    throw new RuleScriptException(
                        $"'{file.Text}.' has to be followed by something about the file, such as Size.",
                        Current.Line);

                var property = Take();
                if (!RuleScriptVocabulary.IsProperty(property.Text))
                    throw new RuleScriptException(
                        $"There is nothing called '{property.Text}' about a file. The list is: " +
                        string.Join(", ", RuleScriptVocabulary.Properties.Select(p => p.Name)) + ".",
                        property.Line);

                return new PropertyExpr(side, RuleScriptVocabulary.CanonicalProperty(property.Text));
            }

            return ReadCall();
        }

        /// <summary>A call, with its name and arguments checked against the vocabulary.</summary>
        private CallExpr ReadCall()
        {
            var name = Take();
            if (RuleScriptVocabulary.FunctionNamed(name.Text) is not { } function)
                throw new RuleScriptException(
                    $"There is nothing called '{name.Text}'. What can be written is: " +
                    string.Join(", ", RuleScriptVocabulary.Functions.Select(f => f.Name)) +
                    ", File1, File2, and the things about a file.", name.Line);

            Expect("(");
            var arguments = new List<ScriptExpr>();
            if (!TakeIf(")"))
            {
                do { arguments.Add(ReadOr()); } while (TakeIf(","));
                Expect(")");
            }

            if (arguments.Count != function.Arity)
                throw new RuleScriptException(
                    $"{function.Name} takes {Count(function.Arity)}, and {Count(arguments.Count)} " +
                    "were given.", name.Line);

            if (function.TakesFile && arguments[0] is not FileExpr)
                throw new RuleScriptException(
                    $"{function.Name} has to be given File1 or File2.", name.Line);

            return new CallExpr(RuleScriptVocabulary.CanonicalFunction(name.Text), arguments);
        }

        private static string Count(int n) => n == 1 ? "one thing" : $"{n} things";

        private static ScriptSide? SideOf(string text) =>
            string.Equals(text, "File1", StringComparison.OrdinalIgnoreCase) ? ScriptSide.File1
            : string.Equals(text, "File2", StringComparison.OrdinalIgnoreCase) ? ScriptSide.File2
            : null;
    }
}
