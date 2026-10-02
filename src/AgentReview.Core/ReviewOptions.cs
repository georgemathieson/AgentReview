namespace AgentReview.Core;

public sealed class ReviewOptions
{
    /// <summary>Lines of context around a change when no enclosing code unit can be found.</summary>
    public int ContextLines { get; set; } = 5;

    /// <summary>Largest method/type/block shown in full. Bigger units show their signature plus the changed area.</summary>
    public int MaxUnitLines { get; set; } = 500;

    /// <summary>Non-C# files up to this many lines are always shown in full.</summary>
    public int SmallFileLines { get; set; } = 150;

    /// <summary>Largest added or deleted file shown in full.</summary>
    public int MaxWholeFileLines { get; set; } = 2000;

    /// <summary>Files whose content is listed but not shown (lock files, minified and generated output).</summary>
    public List<string> OmitContentPatterns { get; set; } =
    [
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "packages.lock.json", "*.min.js", "*.min.css", "*.map",
    ];
}
