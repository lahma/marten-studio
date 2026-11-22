using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DataStudio.Analyzers;

/// <summary>
/// DS1305: a number or a date becomes text in the server's culture, where CA1305 does not look.
/// </summary>
/// <remarks>
/// <para>
/// The studio writes its numbers and dates the same in every culture (#99). CA1305 holds that for a call with an
/// <see cref="System.IFormatProvider" /> overload in a <c>.cs</c> file, and nowhere else: not in an interpolation hole, a
/// concatenation, a <c>StringBuilder.Append</c>, Blazor's <c>AddContent</c> and <c>AddAttribute</c>, or any code of a
/// <c>.razor</c> file, which the compiler sees as generated. #99's twelve faults were all of those kinds. This analyzer reads
/// them by type: a <see langword="double" /> hole depends on the culture and an <see langword="int" /> hole does not, which no
/// scan of the text can tell apart.
/// </para>
/// <para>
/// What depends on the culture: a fractional number (its decimal separator) and a date or time (its default format, its separators
/// and its calendar) whatever the format, except the invariant date formats <c>O</c>, <c>R</c>, <c>s</c> and <c>u</c>; a
/// <see cref="System.TimeSpan" /> written with <c>g</c> or <c>G</c>; an integer written with a format that groups, separates or
/// scales (<c>N0</c>, <c>F1</c>, <c>P</c>, <c>#,0</c>); and an integer the code shows to be negative, a negative constant, a
/// unary minus or a branch of a conditional that is one, because a culture such as fi-FI writes U+2212 for the minus sign.
/// A difference such as <c>count - limit</c> is not taken for negative: every one in <c>src/</c> is guarded by a comparison
/// of the same two values, and the rule would flag them all. The text is in the invariant culture when an interpolated string
/// reaches a handler built with a format provider (<c>string.Create(CultureInfo.InvariantCulture, $"…")</c>,
/// <c>StringBuilder.Append(provider, $"…")</c>) or becomes a <see cref="System.FormattableString" />.
/// </para>
/// <para>
/// Generated code is read only where it maps to a <c>.razor</c> file, and only there does this analyzer also report what CA1305
/// reports in a <c>.cs</c> file: <c>ToString</c>, <c>string.Format</c> and <c>Convert.ToString</c> without a provider.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CurrentCultureTextAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic's id: CA1305's number, for the places CA1305 does not look.</summary>
    public const string DiagnosticId = "DS1305";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "A number or a date becomes text in the server's culture",
        "{0} becomes text in the server's culture here; write it with CultureInfo.InvariantCulture",
        "Globalization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The studio writes numbers and dates the same in every culture. Wrap the interpolated string in "
            + "string.Create(CultureInfo.InvariantCulture, ...), or write the value with ToString(CultureInfo.InvariantCulture).");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var types = new KnownTypes(start.Compilation);

            start.RegisterOperationAction(x => AnalyzeHole(x, types), OperationKind.Interpolation);
            start.RegisterOperationAction(x => AnalyzeHandlerHole(x, types), OperationKind.InterpolatedStringAppendFormatted);
            start.RegisterOperationAction(x => AnalyzeConcatenation(x, types), OperationKind.Binary);
            start.RegisterOperationAction(x => AnalyzeCompoundConcatenation(x, types), OperationKind.CompoundAssignment);
            start.RegisterOperationAction(x => AnalyzeInvocation(x, types), OperationKind.Invocation);
        });
    }

    private static void AnalyzeHole(OperationAnalysisContext context, KnownTypes types)
    {
        var hole = (IInterpolationOperation) context.Operation;

        if (IsFormattedWithProvider(hole.Parent, types))
        {
            return;
        }

        IOperation value = WithoutConversions(hole.Expression);
        Report(context, hole, Dependence(value, value.Type, Constant(hole.FormatString), types));
    }

    private static void AnalyzeHandlerHole(OperationAnalysisContext context, KnownTypes types)
    {
        var hole = (IInterpolatedStringAppendOperation) context.Operation;

        if (hole.AppendCall is not IInvocationOperation call || IsFormattedWithProvider(hole.Parent, types)
            || Argument(call, "value") is not { } value)
        {
            return;
        }

        value = WithoutConversions(value);
        Report(context, hole, Dependence(value, value.Type, Argument(call, "format") is { } format ? Constant(format) : null, types));
    }

    private static void AnalyzeConcatenation(OperationAnalysisContext context, KnownTypes types)
    {
        var binary = (IBinaryOperation) context.Operation;

        if (binary.OperatorKind != BinaryOperatorKind.Add || binary.Type?.SpecialType != SpecialType.System_String)
        {
            return;
        }

        ReportValue(context, binary.LeftOperand, types);
        ReportValue(context, binary.RightOperand, types);
    }

    private static void AnalyzeCompoundConcatenation(OperationAnalysisContext context, KnownTypes types)
    {
        var assignment = (ICompoundAssignmentOperation) context.Operation;

        if (assignment.OperatorKind == BinaryOperatorKind.Add && assignment.Target.Type?.SpecialType == SpecialType.System_String)
        {
            ReportValue(context, assignment.Value, types);
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownTypes types)
    {
        var invocation = (IInvocationOperation) context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        INamedTypeSymbol? owner = method.ContainingType;

        if (Is(owner, types.RenderTreeBuilder))
        {
            // @value in markup and value="@x" on an element: Blazor boxes the value and writes it with ToString().
            if (method.Name is "AddContent" or "AddAttribute")
            {
                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument.Parameter?.Type.SpecialType == SpecialType.System_Object)
                    {
                        ReportValue(context, argument.Value, types);
                    }
                }
            }

            return;
        }

        if (Is(owner, types.StringBuilder) || DerivesFrom(owner, types.TextWriter) || owner?.SpecialType == SpecialType.System_String)
        {
            // StringBuilder.Append(double) and TextWriter.Write(double) have no provider overload, so CA1305 never sees them;
            // string.Concat(object) and string.Join(", ", values) neither.
            if (method.Name is "Append" or "Insert" or "AppendJoin" or "Write" or "WriteLine" or "Concat" or "Join"
                || (method.Name == "Format" && owner?.SpecialType == SpecialType.System_String && context.IsGeneratedCode))
            {
                if (!method.Parameters.Any(x => Is(x.Type, types.FormatProvider)))
                {
                    ReportArguments(context, invocation, types);
                }
            }

            return;
        }

        if (Is(owner, types.BindConverter) && method.Name == "FormatValue")
        {
            // @bind writes the value with BindConverter.FormatValue, in the current culture unless @bind:culture names one.
            if (Argument(invocation, "culture") is not { } culture || culture.ConstantValue is { HasValue: true, Value: null })
            {
                if (Argument(invocation, "value") is { } bound)
                {
                    bound = WithoutConversions(bound);
                    Report(context, bound, Dependence(bound, bound.Type, Argument(invocation, "format") is { } format ? Constant(format) : null, types));
                }
            }

            return;
        }

        // CA1305's own ground, which it does not cover in generated code; Report keeps what maps to a .razor file.
        if (!context.IsGeneratedCode || method.Parameters.Any(x => Is(x.Type, types.FormatProvider)))
        {
            return;
        }

        if (method.Name == "ToString" && invocation.Instance is { } receiver)
        {
            receiver = WithoutConversions(receiver);
            Report(context, invocation, Dependence(receiver, receiver.Type, Argument(invocation, "format") is { } format ? Constant(format) : null, types));
        }
        else if (method.Name == "ToString" && Is(owner, types.Convert) && Argument(invocation, "value") is { } converted)
        {
            ReportValue(context, converted, types);
        }
    }

    /// <summary>
    /// Whether <paramref name="syntax" /> maps, through the <c>#line</c> directives Razor writes, to a <c>.razor</c> file. Razor maps the
    /// expressions a file wrote and hides the calls it wraps them in, so this is asked of the value reported, never of its call.
    /// </summary>
    private static bool IsRazor(SyntaxNode syntax)
    {
        FileLinePositionSpan span = syntax.GetLocation().GetMappedLineSpan();

        return span.HasMappedPath && span.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the interpolated string <paramref name="text" /> is written with a format provider: it reaches a handler whose
    /// constructor takes one, or it becomes a <see cref="System.FormattableString" /> or an <see cref="System.IFormattable" />,
    /// whose provider is the reader's to give.
    /// </summary>
    private static bool IsFormattedWithProvider(IOperation? text, KnownTypes types)
    {
        IOperation? parent = text?.Parent;

        while (parent is IInterpolatedStringAdditionOperation or IParenthesizedOperation)
        {
            parent = parent.Parent;
        }

        return parent switch
        {
            IInterpolatedStringHandlerCreationOperation creation =>
                creation.HandlerCreation is IObjectCreationOperation { Constructor: { } constructor }
                && constructor.Parameters.Any(x => Is(x.Type, types.FormatProvider)),
            IConversionOperation conversion => Is(conversion.Type, types.FormattableString) || Is(conversion.Type, types.Formattable),
            _ => false,
        };
    }

    private static void ReportArguments(OperationAnalysisContext context, IInvocationOperation invocation, KnownTypes types)
    {
        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            IParameterSymbol? parameter = argument.Parameter;

            // An index, a count or a separator is not what is written.
            if (parameter is null || parameter.Name is "index" or "startIndex" or "count" or "repeatCount" or "separator" or "format")
            {
                continue;
            }

            if (argument.Value is IArrayCreationOperation { Initializer: { } initializer } && argument.ArgumentKind == ArgumentKind.ParamArray)
            {
                foreach (IOperation element in initializer.ElementValues)
                {
                    ReportValue(context, element, types);
                }
            }
            else if (parameter.Type is INamedTypeSymbol { IsGenericType: true } enumerable && Is(enumerable.OriginalDefinition, types.Enumerable))
            {
                ITypeSymbol item = enumerable.TypeArguments[0];
                Report(context, argument, Dependence(null, item, null, types));
            }
            else
            {
                ReportValue(context, argument.Value, types);
            }
        }
    }

    private static void ReportValue(OperationAnalysisContext context, IOperation operand, KnownTypes types)
    {
        IOperation value = WithoutConversions(operand);
        Report(context, value, Dependence(value, value.Type, null, types));
    }

    /// <summary>Reports <paramref name="dependence" /> at <paramref name="at" />: in hand-written code, and in generated code where it maps to a <c>.razor</c> file.</summary>
    private static void Report(OperationAnalysisContext context, IOperation at, string? dependence)
    {
        if (dependence is not null && (!context.IsGeneratedCode || IsRazor(at.Syntax)))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, at.Syntax.GetLocation(), dependence));
        }
    }

    /// <summary>
    /// How writing <paramref name="type" /> with <paramref name="format" /> depends on the culture, said as the diagnostic says
    /// it, or <see langword="null" /> when it does not.
    /// </summary>
    /// <param name="value">The value, when there is one to read for a minus sign.</param>
    /// <param name="type">The value's type.</param>
    /// <param name="format">The format: <see langword="null" /> for none, <see cref="Unknown" /> for one that is not a constant.</param>
    /// <param name="types">The compilation's types.</param>
    private static string? Dependence(IOperation? value, ITypeSymbol? type, string? format, KnownTypes types)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        string name = type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? string.Empty;
        string formatted = ReferenceEquals(format, Unknown) ? "This " + name + ", with a format that is not a constant," : "This " + name + " written as \"" + format + "\"";

        switch (types.KindOf(type))
        {
            case ValueKind.Fractional:
                return format is null ? "This " + name : formatted;

            case ValueKind.Date:
                return format is "O" or "o" or "R" or "r" or "s" or "u" ? null : format is null ? "This " + name : formatted;

            case ValueKind.Duration:
                return format is "g" or "G" || ReferenceEquals(format, Unknown) ? formatted : null;

            case ValueKind.Signed:
                return IsCulturalNumberFormat(format) ? formatted : value is not null && IsNegative(value) ? "This negative " + name : null;

            case ValueKind.Unsigned:
                return IsCulturalNumberFormat(format) ? formatted : null;

            default:
                return null;
        }
    }

    /// <summary>Marks a format that is not a constant, so its letters cannot be read.</summary>
    private static readonly string Unknown = new('?', 1);

    private static string? Constant(IOperation? operation) =>
        operation is null ? null : operation.ConstantValue is { HasValue: true, Value: var value } ? value as string : Unknown;

    /// <summary>
    /// Whether an integer written with <paramref name="format" /> depends on the culture: a standard currency, exponent, fixed,
    /// number or percent format, or a custom one with a decimal point, a group separator, a percent or a per mille sign.
    /// </summary>
    private static bool IsCulturalNumberFormat(string? format)
    {
        if (format is null || format.Length == 0)
        {
            return false;
        }

        if (ReferenceEquals(format, Unknown))
        {
            return true;
        }

        if (char.IsLetter(format[0]) && format.Skip(1).All(char.IsDigit))
        {
            return "CcEeFfNnPp".IndexOf(format[0]) >= 0;
        }

        char quote = '\0';

        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];

            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c is '.' or ',' or '%' or '‰')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the code shows <paramref name="operation" /> to be negative: a negative constant, a unary minus, or a branch that is one.</summary>
    private static bool IsNegative(IOperation operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
        {
            operation = operation is IConversionOperation conversion ? conversion.Operand : ((IParenthesizedOperation) operation).Operand;
        }

        if (operation.ConstantValue is { HasValue: true, Value: { } constant })
        {
            return constant switch
            {
                sbyte x => x < 0,
                short x => x < 0,
                int x => x < 0,
                long x => x < 0,
                float x => x < 0,
                double x => x < 0,
                decimal x => x < 0,
                _ => false,
            };
        }

        return operation switch
        {
            IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } => true,
            IConditionalOperation conditional => IsNegative(conditional.WhenTrue) || (conditional.WhenFalse is { } otherwise && IsNegative(otherwise)),
            ICoalesceOperation coalesce => IsNegative(coalesce.Value) || IsNegative(coalesce.WhenNull),
            ISwitchExpressionOperation choice => choice.Arms.Any(x => IsNegative(x.Value)),
            _ => false,
        };
    }

    /// <summary>The operand under its implicit conversions and any conversion to <see langword="object" />: the value that is written.</summary>
    private static IOperation WithoutConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion
               && (conversion.IsImplicit || conversion.Type?.SpecialType == SpecialType.System_Object))
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static IOperation? Argument(IInvocationOperation invocation, string parameter) =>
        invocation.Arguments.FirstOrDefault(x => x.Parameter?.Name == parameter && x.ArgumentKind != ArgumentKind.DefaultValue)?.Value;

    private static bool Is(ITypeSymbol? type, ITypeSymbol? known) =>
        known is not null && SymbolEqualityComparer.Default.Equals(type, known);

    private static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol? known)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (Is(current, known))
            {
                return true;
            }
        }

        return false;
    }

    private enum ValueKind
    {
        None,
        Fractional,
        Date,
        Duration,
        Signed,
        Unsigned,
    }

    /// <summary>The types the rule names, read once a compilation; one that the compilation lacks is <see langword="null" />.</summary>
    private sealed class KnownTypes(Compilation compilation)
    {
        public INamedTypeSymbol? FormatProvider { get; } = compilation.GetTypeByMetadataName("System.IFormatProvider");

        public INamedTypeSymbol? FormattableString { get; } = compilation.GetTypeByMetadataName("System.FormattableString");

        public INamedTypeSymbol? Formattable { get; } = compilation.GetTypeByMetadataName("System.IFormattable");

        public INamedTypeSymbol? Enumerable { get; } = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");

        public INamedTypeSymbol? StringBuilder { get; } = compilation.GetTypeByMetadataName("System.Text.StringBuilder");

        public INamedTypeSymbol? TextWriter { get; } = compilation.GetTypeByMetadataName("System.IO.TextWriter");

        public INamedTypeSymbol? Convert { get; } = compilation.GetTypeByMetadataName("System.Convert");

        public INamedTypeSymbol? RenderTreeBuilder { get; } = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder");

        public INamedTypeSymbol? BindConverter { get; } = compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.BindConverter");

        private readonly ImmutableArray<(INamedTypeSymbol? Type, ValueKind Kind)> _named =
        [
            (compilation.GetTypeByMetadataName("System.Half"), ValueKind.Fractional),
            (compilation.GetTypeByMetadataName("System.Runtime.InteropServices.NFloat"), ValueKind.Fractional),
            (compilation.GetTypeByMetadataName("System.DateTimeOffset"), ValueKind.Date),
            (compilation.GetTypeByMetadataName("System.DateOnly"), ValueKind.Date),
            (compilation.GetTypeByMetadataName("System.TimeOnly"), ValueKind.Date),
            (compilation.GetTypeByMetadataName("System.TimeSpan"), ValueKind.Duration),
            (compilation.GetTypeByMetadataName("System.Int128"), ValueKind.Signed),
            (compilation.GetTypeByMetadataName("System.Numerics.BigInteger"), ValueKind.Signed),
            (compilation.GetTypeByMetadataName("System.UInt128"), ValueKind.Unsigned),
        ];

        public ValueKind KindOf(ITypeSymbol? type)
        {
            switch (type?.SpecialType)
            {
                case null:
                    return ValueKind.None;
                case SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal:
                    return ValueKind.Fractional;
                case SpecialType.System_DateTime:
                    return ValueKind.Date;
                case SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_IntPtr:
                    return ValueKind.Signed;
                case SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or SpecialType.System_UIntPtr:
                    return ValueKind.Unsigned;
            }

            foreach ((INamedTypeSymbol? known, ValueKind kind) in _named)
            {
                if (Is(type, known))
                {
                    return kind;
                }
            }

            return ValueKind.None;
        }
    }
}