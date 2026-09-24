using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace MolaGPT.Presentation.Visuals;

public sealed class MathSyntaxException(string message) : Exception(message);

/// <summary>
/// A small, closed math language for plot expressions: numbers, named
/// variables, + - * / ^, a fixed function list and pi / e. Nothing a model
/// writes here can reach anything but <see cref="Math"/> — the expression is
/// parsed into our own tree and compiled from that, never evaluated as code.
///
/// Tolerant where writers of math are loose: implicit multiplication (2x,
/// 3(x+1), (x+1)(x-1)), <c>**</c> for power, Unicode operators, <c>sin^2(x)</c>,
/// <c>|x|</c>. Strict where guessing would be wrong: a function needs brackets.
///
/// Sum <c>sum(k=1, n, expr)</c> and product <c>prod(…)</c> are the only forms
/// that bind a variable: a series whose number of terms follows a slider can't
/// be written any other way. Bounds are floored, at most <see cref="MaxTerms"/>
/// terms, no nesting — so the work per sample point stays bounded while a slider
/// is dragged.
/// </summary>
public sealed class MathExpression
{
    public const int MaxTerms = 1000;

    /// <summary>Tolerance before flooring bounds: a slider snapping on a
    /// fractional step can land 3 on 2.9999999999999996.</summary>
    private const double BoundEpsilon = 1e-9;

    private MathExpression(Node root, string source)
    {
        Root = root;
        Source = source;
    }

    internal Node Root { get; }
    public string Source { get; }

    /// <summary>Free variable names, in first-seen order. Summation indices are
    /// bound, not free.</summary>
    public IReadOnlyList<string> Variables
    {
        get
        {
            var names = new List<string>();
            Collect(Root, names, new HashSet<string>());
            return names;
        }
    }

    /// <summary>Free variables used in sum / product bounds: sliders that only
    /// make sense as integers.</summary>
    public IReadOnlySet<string> CountingVariables
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            CollectCounting(Root, names);
            return names;
        }
    }

    /// <param name="knownNames">Names that may legitimately appear (plot
    /// variables and slider parameters). Used only to split run-together
    /// products such as <c>ax</c> or <c>sinx</c>.</param>
    public static MathExpression Parse(string text, IReadOnlyCollection<string>? knownNames = null)
    {
        var parser = new Parser(Normalize(text), knownNames ?? Array.Empty<string>());
        var root = parser.ParseAll();
        return new MathExpression(root, text);
    }

    /// <summary>Compiles to a delegate over a value array laid out as
    /// <paramref name="variableOrder"/>. Throws if the expression uses a name
    /// outside that list.</summary>
    public Func<double[], double> Compile(IReadOnlyList<string> variableOrder)
    {
        var values = Expression.Parameter(typeof(double[]), "v");
        var body = Emit(Root, values, variableOrder);
        return Expression.Lambda<Func<double[], double>>(body, values).Compile();
    }

    public string ToLatex() => Latex(Root, 0);

    // ---- tree ------------------------------------------------------------

    internal abstract record Node;
    internal sealed record Num(double Value) : Node;
    internal sealed record Var(string Name) : Node;
    internal sealed record Const(string Name, double Value) : Node;
    internal sealed record Neg(Node Operand) : Node;
    internal sealed record Bin(char Op, Node Left, Node Right, bool Implicit = false) : Node;
    internal sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;
    /// <summary>Sum (<see cref="Product"/> false) or product: <see cref="Index"/>
    /// takes each integer from <see cref="From"/> to <see cref="To"/>.</summary>
    internal sealed record Iter(bool Product, string Index, Node From, Node To, Node Body) : Node;

    private static void Collect(Node node, List<string> names, IReadOnlySet<string> bound)
    {
        switch (node)
        {
            case Var v when !bound.Contains(v.Name) && !names.Contains(v.Name): names.Add(v.Name); break;
            case Neg n: Collect(n.Operand, names, bound); break;
            case Bin b: Collect(b.Left, names, bound); Collect(b.Right, names, bound); break;
            case Call c: foreach (var a in c.Args) Collect(a, names, bound); break;
            case Iter it:
                Collect(it.From, names, bound);
                Collect(it.To, names, bound);
                Collect(it.Body, names, new HashSet<string>(bound, StringComparer.Ordinal) { it.Index });
                break;
        }
    }

    private static void CollectCounting(Node node, HashSet<string> names)
    {
        switch (node)
        {
            case Neg n: CollectCounting(n.Operand, names); break;
            case Bin b: CollectCounting(b.Left, names); CollectCounting(b.Right, names); break;
            case Call c: foreach (var a in c.Args) CollectCounting(a, names); break;
            case Iter it:
                var bounds = new List<string>();
                Collect(it.From, bounds, new HashSet<string>());
                Collect(it.To, bounds, new HashSet<string>());
                names.UnionWith(bounds);
                break;
        }
    }

    // ---- functions ---------------------------------------------------------

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["arcsin"] = "asin", ["arccos"] = "acos", ["arctan"] = "atan",
        ["log10"] = "lg", ["abs"] = "abs", ["sgn"] = "sign", ["√"] = "sqrt",
        ["ceiling"] = "ceil", ["arsinh"] = "asinh", ["arcosh"] = "acosh", ["artanh"] = "atanh",
        ["factorial"] = "fact", ["product"] = "prod",
    };

    /// <summary>name → (min args, max args).</summary>
    private static readonly Dictionary<string, (int Min, int Max)> Functions = new(StringComparer.Ordinal)
    {
        ["sin"] = (1, 1), ["cos"] = (1, 1), ["tan"] = (1, 1),
        ["cot"] = (1, 1), ["sec"] = (1, 1), ["csc"] = (1, 1),
        ["asin"] = (1, 1), ["acos"] = (1, 1), ["atan"] = (1, 2), ["atan2"] = (2, 2),
        ["sinh"] = (1, 1), ["cosh"] = (1, 1), ["tanh"] = (1, 1),
        ["asinh"] = (1, 1), ["acosh"] = (1, 1), ["atanh"] = (1, 1),
        ["sqrt"] = (1, 1), ["cbrt"] = (1, 1), ["abs"] = (1, 1), ["exp"] = (1, 1),
        ["ln"] = (1, 1), ["log"] = (1, 2), ["lg"] = (1, 1), ["log2"] = (1, 1),
        ["floor"] = (1, 1), ["ceil"] = (1, 1), ["round"] = (1, 1), ["sign"] = (1, 1),
        ["min"] = (2, 8), ["max"] = (2, 8), ["pow"] = (2, 2), ["mod"] = (2, 2), ["hypot"] = (2, 2),
        ["fact"] = (1, 1), ["sum"] = (4, 4), ["prod"] = (4, 4),
    };

    private static readonly HashSet<string> Iterators = new(StringComparer.Ordinal) { "sum", "prod" };

    // Sum and product bind a variable; they take no part in "sinx → sin(x)" splitting.
    private static readonly string[] SplittableFunctions =
        Functions.Keys.Where(k => !Iterators.Contains(k)).OrderByDescending(k => k.Length).ToArray();

    private static string? ResolveFunction(string name)
    {
        var lower = name.ToLowerInvariant();
        if (Aliases.TryGetValue(lower, out var alias)) lower = alias;
        return Functions.ContainsKey(lower) ? lower : null;
    }

    private static bool TryConstant(string name, out double value)
    {
        switch (name)
        {
            case "pi" or "π" or "PI": value = Math.PI; return true;
            case "e": value = Math.E; return true;
            case "tau" or "τ": value = Math.Tau; return true;
            default: value = 0; return false;
        }
    }

    // ---- normalize + lex -------------------------------------------------------

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '−' or '–' or '—': sb.Append('-'); break;
                case '×' or '·' or '∙' or '⋅': sb.Append('*'); break;
                case '÷': sb.Append('/'); break;
                case '（': sb.Append('('); break;
                case '）': sb.Append(')'); break;
                case '，': sb.Append(','); break;
                case '²': sb.Append("^2"); break;
                case '³': sb.Append("^3"); break;
                case 'π': sb.Append("(pi)"); break;
                case '√': sb.Append(" sqrt"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString().Replace("**", "^", StringComparison.Ordinal);
    }

    private enum TokenKind { Number, Ident, Op, End }

    private readonly record struct Token(TokenKind Kind, string Text, double Value = 0);

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        /// <summary>Names allowed to appear; a summation index joins while its body is parsed.</summary>
        private readonly HashSet<string> _known;
        private int _index;
        private bool _inIteration;

        public Parser(string text, IReadOnlyCollection<string> known)
        {
            _known = new HashSet<string>(known, StringComparer.Ordinal);
            _tokens = Lex(text);
        }

        private Token Peek => _tokens[_index];
        private Token Next() => _tokens[_index++];
        private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;

        public Node ParseAll()
        {
            if (Peek.Kind == TokenKind.End) throw new MathSyntaxException("表达式为空");
            var node = ParseAdditive();
            if (Peek.Kind != TokenKind.End)
                throw new MathSyntaxException($"无法理解「{Peek.Text}」");
            return node;
        }

        private Node ParseAdditive()
        {
            var left = ParseTerm();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next().Text[0];
                left = new Bin(op, left, ParseTerm());
            }

            return left;
        }

        private Node ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                if (IsOp("*") || IsOp("/"))
                {
                    var op = Next().Text[0];
                    left = new Bin(op, left, ParseUnary());
                }
                else if (Peek.Kind is TokenKind.Number or TokenKind.Ident || IsOp("("))
                {
                    left = new Bin('*', left, ParsePower(), Implicit: true);
                }
                else
                {
                    return left;
                }
            }
        }

        private Node ParseUnary()
        {
            if (IsOp("-")) { Next(); return new Neg(ParseUnary()); }
            if (IsOp("+")) { Next(); return ParseUnary(); }
            return ParsePower();
        }

        private Node ParsePower()
        {
            var baseNode = ParsePrimary();
            // Factorial is postfix and binds tighter than power: (2k+1)!, n!^2.
            while (IsOp("!"))
            {
                Next();
                baseNode = new Call("fact", [baseNode]);
            }

            if (!IsOp("^")) return baseNode;
            Next();
            return new Bin('^', baseNode, ParseUnary());
        }

        private Node ParsePrimary()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.Number:
                    return new Num(token.Value);
                case TokenKind.Ident:
                    return ParseIdentifier(token.Text);
                case TokenKind.Op when token.Text == "(":
                {
                    var inner = ParseAdditive();
                    Expect(")");
                    return inner;
                }
                case TokenKind.Op when token.Text == "|":
                {
                    var inner = ParseAdditive();
                    Expect("|");
                    return new Call("abs", [inner]);
                }
                case TokenKind.End:
                    throw new MathSyntaxException("表达式不完整");
                default:
                    throw new MathSyntaxException($"这里不应出现「{token.Text}」");
            }
        }

        private Node ParseIdentifier(string name)
        {
            if (ResolveFunction(name) is { } function)
                return ParseCall(name, function);

            if (_known.Contains(name)) return new Var(name);
            if (TryConstant(name, out var constant)) return new Const(name, constant);
            if (IsOp("(") && name.Length > 1) throw new MathSyntaxException($"未知函数 {name}");

            // "sinx" → sin(x); "ax" → a·x. Only when every piece is a name we
            // actually know, so a typo is still reported as a typo.
            if (SplitRunTogether(name) is { } split) return split;
            return new Var(name);
        }

        private Node ParseCall(string written, string function)
        {
            // sin^2(x) = (sin x)^2 — standard notation, and models use it.
            Node? power = null;
            if (IsOp("^"))
            {
                Next();
                power = ParsePrimary();
            }

            if (!IsOp("("))
            {
                // √x is written without brackets as often as with them.
                if (function == "sqrt" && power is null) return new Call("sqrt", [ParsePower()]);
                throw new MathSyntaxException($"函数 {written} 后需要括号，例如 {written}(x)");
            }
            Next();

            if (Iterators.Contains(function))
            {
                var iter = ParseIteration(written, function == "prod");
                return power is null ? iter : new Bin('^', iter, power);
            }

            var args = new List<Node> { ParseAdditive() };
            while (IsOp(","))
            {
                Next();
                args.Add(ParseAdditive());
            }

            Expect(")");
            var (min, max) = Functions[function];
            if (args.Count < min || args.Count > max)
                throw new MathSyntaxException(min == max
                    ? $"{written} 需要 {min} 个参数"
                    : $"{written} 需要 {min}–{max} 个参数");

            Node call = new Call(function, args);
            return power is null ? call : new Bin('^', call, power);
        }

        /// <summary>After the opening bracket: <c>k=1, n, expr)</c> or <c>k, 1, n, expr)</c>.</summary>
        private Node ParseIteration(string written, bool product)
        {
            if (_inIteration) throw new MathSyntaxException("求和、连乘不能嵌套");
            var usage = $"{written} 的写法是 {written}(k=1, n, 表达式)";
            var head = Next();
            if (head.Kind != TokenKind.Ident || ResolveFunction(head.Text) is not null || TryConstant(head.Text, out _))
                throw new MathSyntaxException(usage);
            if (!IsOp("=") && !IsOp(",")) throw new MathSyntaxException(usage);
            Next();

            var from = ParseAdditive();
            Expect(",");
            var to = ParseAdditive();
            Expect(",");
            var added = _known.Add(head.Text);
            _inIteration = true;
            Node body;
            try
            {
                body = ParseAdditive();
            }
            finally
            {
                _inIteration = false;
                if (added) _known.Remove(head.Text);
            }

            Expect(")");
            if (ConstantValue(from) is { } lo && ConstantValue(to) is { } hi
                && Math.Floor(hi + BoundEpsilon) - Math.Floor(lo + BoundEpsilon) >= MaxTerms)
            {
                throw new MathSyntaxException($"{written} 最多 {MaxTerms} 项");
            }

            return new Iter(product, head.Text, from, to, body);
        }

        private static double? ConstantValue(Node node) => node switch
        {
            Num n => n.Value,
            Const c => c.Value,
            Neg n => -ConstantValue(n.Operand),
            _ => null,
        };

        private Node? SplitRunTogether(string name)
        {
            foreach (var function in SplittableFunctions)
            {
                if (name.Length > function.Length
                    && name.StartsWith(function, StringComparison.Ordinal)
                    && _known.Contains(name[function.Length..]))
                {
                    return new Call(function, [new Var(name[function.Length..])]);
                }
            }

            if (name.Length is < 2 or > 4) return null;
            Node? product = null;
            foreach (var c in name)
            {
                var part = c.ToString();
                Node factor;
                if (_known.Contains(part)) factor = new Var(part);
                else if (part == "e") factor = new Const("e", Math.E);
                else return null;
                product = product is null ? factor : new Bin('*', product, factor, Implicit: true);
            }

            return product;
        }

        private void Expect(string op)
        {
            if (!IsOp(op))
                throw new MathSyntaxException(Peek.Kind == TokenKind.End ? $"缺少「{op}」" : $"此处应为「{op}」");
            Next();
        }

        private static List<Token> Lex(string text)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    var start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    // Exponent only when digits follow — "2e" is two times e.
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        var j = i + 1;
                        if (j < text.Length && text[j] is '+' or '-') j++;
                        if (j < text.Length && char.IsDigit(text[j]))
                        {
                            i = j;
                            while (i < text.Length && char.IsDigit(text[i])) i++;
                        }
                    }

                    var literal = text[start..i];
                    if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        throw new MathSyntaxException($"数字「{literal}」无效");
                    tokens.Add(new Token(TokenKind.Number, literal, value));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(TokenKind.Ident, text[start..i]));
                    continue;
                }

                // "=" only means something inside sum(k=1, …); an equation's
                // equals sign is split off before the text gets here.
                if ("+-*/^(),|!=".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Op, c.ToString()));
                    i++;
                    continue;
                }

                if (c == '[') { tokens.Add(new Token(TokenKind.Op, "(")); i++; continue; }
                if (c == ']') { tokens.Add(new Token(TokenKind.Op, ")")); i++; continue; }

                throw new MathSyntaxException($"不支持的字符「{c}」");
            }

            tokens.Add(new Token(TokenKind.End, "结尾"));
            return tokens;
        }
    }

    // ---- compile ---------------------------------------------------------------

    private static readonly MethodInfo Pow = typeof(Math).GetMethod(nameof(Math.Pow), [typeof(double), typeof(double)])!;

    private static Expression Emit(Node node, ParameterExpression values, IReadOnlyList<string> order)
    {
        switch (node)
        {
            case Num n:
                return Expression.Constant(n.Value);
            case Const c:
                return Expression.Constant(c.Value);
            case Var v:
            {
                var index = IndexOf(order, v.Name);
                if (index < 0) throw new MathSyntaxException($"未知变量 {v.Name}");
                return Expression.ArrayIndex(values, Expression.Constant(index));
            }
            case Neg n:
                return Expression.Negate(Emit(n.Operand, values, order));
            case Bin b:
            {
                var left = Emit(b.Left, values, order);
                var right = Emit(b.Right, values, order);
                return b.Op switch
                {
                    '+' => Expression.Add(left, right),
                    '-' => Expression.Subtract(left, right),
                    '*' => Expression.Multiply(left, right),
                    '/' => Expression.Divide(left, right),
                    '^' => Expression.Call(typeof(MathExpression).GetMethod(nameof(Power), BindingFlags.NonPublic | BindingFlags.Static)!, left, right),
                    _ => throw new MathSyntaxException($"未知运算 {b.Op}"),
                };
            }
            case Call call:
            {
                var args = call.Args.Select(a => Emit(a, values, order)).ToArray();
                return EmitCall(call.Name, args);
            }
            case Iter it:
            {
                // The body is its own delegate over the values plus one slot for the index.
                var inner = order.Append(it.Index).ToArray();
                var scope = Expression.Parameter(typeof(double[]), "s");
                var body = Expression.Lambda<Func<double[], double>>(Emit(it.Body, scope, inner), scope).Compile();
                return Expression.Call(
                    typeof(MathExpression).GetMethod(nameof(Iterate), BindingFlags.NonPublic | BindingFlags.Static)!,
                    Expression.Constant(it.Product),
                    Emit(it.From, values, order),
                    Emit(it.To, values, order),
                    values,
                    Expression.Constant(order.Count),
                    Expression.Constant(body));
            }
            default:
                throw new MathSyntaxException("无法编译表达式");
        }
    }

    /// <summary>Last match: the summation index is appended at the end, so an
    /// index named like a slider wins inside its body.</summary>
    private static int IndexOf(IReadOnlyList<string> order, string name)
    {
        for (var i = order.Count - 1; i >= 0; i--)
            if (string.Equals(order[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static double Iterate(bool product, double from, double to, double[] values, int slot, Func<double[], double> body)
    {
        var lo = Math.Floor(from + BoundEpsilon);
        var hi = Math.Floor(to + BoundEpsilon);
        // Comparisons with NaN are false, so this rejects NaN bounds too.
        if (!(hi - lo < MaxTerms)) return double.NaN;
        var scope = new double[slot + 1];
        Array.Copy(values, scope, Math.Min(values.Length, slot));
        var acc = product ? 1d : 0d;
        for (var k = lo; k <= hi; k += 1)
        {
            scope[slot] = k;
            var term = body(scope);
            acc = product ? acc * term : acc + term;
        }

        return acc;
    }

    private static Expression EmitCall(string name, Expression[] args)
    {
        static Expression M(string method, params Expression[] a) =>
            Expression.Call(typeof(Math).GetMethod(method, a.Select(_ => typeof(double)).ToArray())!, a);
        static Expression H(string method, params Expression[] a) =>
            Expression.Call(typeof(MathExpression).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static, a.Select(_ => typeof(double)).ToArray())!, a);

        return name switch
        {
            "sin" => M("Sin", args), "cos" => M("Cos", args), "tan" => M("Tan", args),
            "cot" => Expression.Divide(Expression.Constant(1d), M("Tan", args)),
            "sec" => Expression.Divide(Expression.Constant(1d), M("Cos", args)),
            "csc" => Expression.Divide(Expression.Constant(1d), M("Sin", args)),
            "asin" => M("Asin", args), "acos" => M("Acos", args),
            "atan" => args.Length == 2 ? M("Atan2", args) : M("Atan", args),
            "atan2" => M("Atan2", args),
            "sinh" => M("Sinh", args), "cosh" => M("Cosh", args), "tanh" => M("Tanh", args),
            "asinh" => M("Asinh", args), "acosh" => M("Acosh", args), "atanh" => M("Atanh", args),
            "sqrt" => M("Sqrt", args), "cbrt" => M("Cbrt", args), "abs" => M("Abs", args),
            "exp" => M("Exp", args), "ln" => M("Log", args),
            "log" => args.Length == 2 ? M("Log", args) : M("Log", args),
            "lg" => M("Log10", args), "log2" => M("Log2", args),
            "floor" => M("Floor", args), "ceil" => M("Ceiling", args),
            "round" => H(nameof(RoundHalfAway), args), "sign" => H(nameof(Sign), args),
            "min" => args.Skip(1).Aggregate(args[0], (acc, a) => M("Min", acc, a)),
            "max" => args.Skip(1).Aggregate(args[0], (acc, a) => M("Max", acc, a)),
            "pow" => Expression.Call(typeof(MathExpression).GetMethod(nameof(Power), BindingFlags.NonPublic | BindingFlags.Static)!, args),
            "mod" => H(nameof(Mod), args),
            "hypot" => H(nameof(Hypot), args),
            "fact" => H(nameof(Factorial), args),
            _ => throw new MathSyntaxException($"未知函数 {name}"),
        };
    }

    /// <summary>Integers multiply out (undefined for negative integers);
    /// non-integers take Γ(x+1), so x! also plots as a continuous curve.</summary>
    private static double Factorial(double x)
    {
        if (double.IsNaN(x)) return x;
        var n = Math.Round(x);
        if (Math.Abs(x - n) > BoundEpsilon) return Gamma(x + 1);
        if (n < 0) return double.NaN;
        if (n > 170) return double.PositiveInfinity;
        var acc = 1d;
        for (var i = 2; i <= (int)n; i++) acc *= i;
        return acc;
    }

    private static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    /// <summary>Lanczos approximation (g = 7), reflection below 0.5.</summary>
    private static double Gamma(double x)
    {
        if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
        var z = x - 1;
        var a = Lanczos[0];
        for (var i = 1; i < Lanczos.Length; i++) a += Lanczos[i] / (z + i);
        var t = z + 7.5;
        return Math.Sqrt(2 * Math.PI) * Math.Pow(t, z + 0.5) * Math.Exp(-t) * a;
    }

    /// <summary>Math.Pow returns NaN for a negative base with a fractional
    /// exponent. Odd roots written as fractions (x^(1/3)) should still draw the
    /// negative branch, as every graphing calculator does.</summary>
    private static double Power(double b, double e)
    {
        if (b >= 0 || Math.Abs(e - Math.Round(e)) < 1e-12) return Math.Pow(b, e);
        var reciprocal = 1 / e;
        var odd = Math.Round(reciprocal);
        if (Math.Abs(reciprocal - odd) < 1e-9 && ((long)odd & 1) == 1) return -Math.Pow(-b, e);
        return double.NaN;
    }

    private static double Mod(double a, double b) => a - b * Math.Floor(a / b);
    private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);
    private static double Sign(double a) => double.IsNaN(a) ? double.NaN : Math.Sign(a);
    private static double RoundHalfAway(double a) => Math.Round(a, MidpointRounding.AwayFromZero);

    // ---- LaTeX -------------------------------------------------------------------

    private static readonly Dictionary<string, string> Greek = new(StringComparer.Ordinal)
    {
        ["alpha"] = @"\alpha", ["beta"] = @"\beta", ["gamma"] = @"\gamma", ["delta"] = @"\delta",
        ["epsilon"] = @"\epsilon", ["theta"] = @"\theta", ["lambda"] = @"\lambda", ["mu"] = @"\mu",
        ["sigma"] = @"\sigma", ["phi"] = @"\varphi", ["omega"] = @"\omega", ["rho"] = @"\rho",
        ["tau"] = @"\tau", ["kappa"] = @"\kappa", ["eta"] = @"\eta", ["xi"] = @"\xi", ["psi"] = @"\psi",
    };

    private static int Precedence(Node node) => node switch
    {
        Bin { Op: '+' or '-' } => 1,
        Bin { Op: '*' or '/' } => 2,
        // ∑ swallows everything to its right; it sits unbracketed only inside a product.
        Iter => 2,
        Neg => 3,
        Bin { Op: '^' } => 4,
        _ => 5,
    };

    private static string Latex(Node node, int parent)
    {
        var text = node switch
        {
            Num n => FormatNumber(n.Value),
            Const c => c.Name is "pi" or "PI" or "π" ? @"\pi" : c.Name is "tau" or "τ" ? @"\tau" : "e",
            Var v => VariableLatex(v.Name),
            Neg n => "-" + Latex(n.Operand, 3),
            Bin { Op: '+' } b => LeftLatex(b.Left, 1) + " + " + Latex(b.Right, 1),
            Bin { Op: '-' } b => LeftLatex(b.Left, 1) + " - " + Latex(b.Right, 2),
            Bin { Op: '*' } b => LeftLatex(b.Left, 2) + ((b.Implicit && !(b.Left is Num && b.Right is Num)) || Juxtapose(b) ? " " : @" \cdot ") + Latex(b.Right, 2),
            Bin { Op: '/' } b => @"\frac{" + Latex(b.Left, 0) + "}{" + Latex(b.Right, 0) + "}",
            Bin { Op: '^' } b => PowerLatex(b),
            Call c => CallLatex(c),
            Iter it => (it.Product ? @"\prod" : @"\sum") + "_{" + VariableLatex(it.Index) + "=" + Latex(it.From, 0)
                + "}^{" + Latex(it.To, 0) + "} " + Latex(it.Body, 2),
            _ => "?",
        };

        return Precedence(node) < parent && (node is not Bin { Op: '/' } || parent >= 5)
            ? @"\left(" + text + @"\right)"
            : text;
    }

    /// <summary>Brackets a ∑ on the left, or a following "+ 1" reads as part of its body.</summary>
    private static string LeftLatex(Node node, int parent) =>
        node is Iter ? @"\left(" + Latex(node, 0) + @"\right)" : Latex(node, parent);

    private static string PowerLatex(Bin b)
    {
        var exponent = Latex(b.Right, 0);
        if (b.Left is Call { Name: "sin" or "cos" or "tan" or "cot" or "sec" or "csc" or "sinh" or "cosh" or "tanh" } trig
            && b.Right is Num)
        {
            return $@"\{trig.Name}^{{{exponent}}}\left({Latex(trig.Args[0], 0)}\right)";
        }

        return Latex(b.Left, 5) + "^{" + exponent + "}";
    }

    private static bool Juxtapose(Bin b) =>
        b.Left is Num or Const or Var or Bin { Op: '^' }
        && b.Right is Var or Const or Call or Bin { Op: '^', Left: Var or Const }
        && !(b.Left is Num && b.Right is Num);

    private static string CallLatex(Call c)
    {
        var a = c.Args.Select(x => Latex(x, 0)).ToArray();
        return c.Name switch
        {
            "sqrt" => @"\sqrt{" + a[0] + "}",
            "cbrt" => @"\sqrt[3]{" + a[0] + "}",
            "abs" => @"\left|" + a[0] + @"\right|",
            "floor" => @"\lfloor " + a[0] + @" \rfloor",
            "ceil" => @"\lceil " + a[0] + @" \rceil",
            "exp" => "e^{" + a[0] + "}",
            "fact" => Latex(c.Args[0], 5) + "!",
            "pow" => Latex(c.Args[0], 5) + "^{" + a[1] + "}",
            "log" when a.Length == 2 => @"\log_{" + a[1] + @"}\left(" + a[0] + @"\right)",
            "lg" => @"\lg\left(" + a[0] + @"\right)",
            "log2" => @"\log_{2}\left(" + a[0] + @"\right)",
            "log" or "ln" => @"\ln\left(" + a[0] + @"\right)",
            "asin" => @"\arcsin\left(" + a[0] + @"\right)",
            "acos" => @"\arccos\left(" + a[0] + @"\right)",
            "atan" when a.Length == 1 => @"\arctan\left(" + a[0] + @"\right)",
            "sin" or "cos" or "tan" or "cot" or "sec" or "csc" or "sinh" or "cosh" or "tanh" or "min" or "max"
                => "\\" + c.Name + @"\left(" + string.Join(", ", a) + @"\right)",
            "mod" => a[0] + @" \bmod " + a[1],
            _ => @"\mathrm{" + c.Name + @"}\left(" + string.Join(", ", a) + @"\right)",
        };
    }

    private static string VariableLatex(string name)
    {
        if (Greek.TryGetValue(name, out var greek)) return greek;
        if (name.Length == 1) return name;
        var split = name.Length - 1;
        while (split > 0 && char.IsDigit(name[split])) split--;
        if (split < name.Length - 1 && split == 0)
            return name[..1] + "_{" + name[1..] + "}";
        return @"\mathit{" + name.Replace("_", @"\_", StringComparison.Ordinal) + "}";
    }

    public static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (Math.Abs(value) >= 1e6 || (Math.Abs(value) < 1e-4 && value != 0))
            return value.ToString("0.###e+0", CultureInfo.InvariantCulture);
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
