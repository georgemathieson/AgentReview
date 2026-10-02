using AgentReview.Core.Output;

namespace AgentReview.Core.Tests;

public class ReviewBuilderTests
{
    private const string OriginalService = """
        using System;

        namespace Shop.Services;

        public class OrderService
        {
            private readonly int _retries = 3;

            /// <summary>
            /// Places an order.
            /// </summary>
            public int PlaceOrder(string customer, int quantity)
            {
                if (quantity <= 0)
                    throw new ArgumentOutOfRangeException(nameof(quantity));

                var total = quantity * 10;
                Console.WriteLine($"Order for {customer}: {total}");
                return total;
            }

            public void Unrelated()
            {
                Console.WriteLine("this method is not touched");
            }

            public void Untouched2()
            {
                Console.WriteLine("1");
                Console.WriteLine("2");
                Console.WriteLine("3");
                Console.WriteLine("4");
                Console.WriteLine("5");
                Console.WriteLine("6");
                Console.WriteLine("7");
                Console.WriteLine("8");
            }

            public int Cancel(int id) => id;
        }

        """;

    private static async Task<ReviewResult> Review(TempGitRepo repo) =>
        await new ReviewBuilder().BuildAsync(repo.Path, "main", "feature", new ReviewOptions());

    [Fact]
    public async Task Doc_comment_change_shows_the_complete_method_as_context()
    {
        using var repo = new TempGitRepo();
        repo.Write("src/OrderService.cs", OriginalService);
        repo.Commit("initial");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Write("src/OrderService.cs", OriginalService.Replace("Places an order.", "Places an order and logs the total."));
        repo.Commit("doc change");

        var review = await Review(repo);
        var file = Assert.Single(review.Files);
        var region = Assert.Single(file.Regions);
        Assert.Equal(["complete method `PlaceOrder(string, int)` in `Shop.Services.OrderService`"], region.Descriptions);

        var md = MarkdownFormatter.Format(review);
        Assert.Contains("- 10    |     /// Places an order.", md);
        Assert.Contains("+    10 |     /// Places an order and logs the total.", md);
        // The whole, unchanged method body is present as context.
        Assert.Contains("  17 17 |         var total = quantity * 10;", md);
        Assert.Contains("  20 20 |     }", md);
        // Unrelated methods are not.
        Assert.DoesNotContain("this method is not touched", md);
        Assert.Contains("unchanged line(s) not shown", md);
    }

    [Fact]
    public async Task Changes_in_two_members_produce_both_complete_members_and_removed_member_uses_base_tree()
    {
        using var repo = new TempGitRepo();
        repo.Write("src/OrderService.cs", OriginalService);
        repo.Commit("initial");
        repo.Git("checkout", "-q", "-b", "feature");
        var changed = OriginalService
            .Replace("""
                    public int Cancel(int id) => id;

                """, "")
            .Replace("private readonly int _retries = 3;", "private readonly int _retries = 5;");
        repo.Write("src/OrderService.cs", changed);
        repo.Commit("change");

        var review = await Review(repo);
        var descriptions = review.Files[0].Regions.SelectMany(r => r.Descriptions).ToList();
        Assert.Contains("complete field `_retries` in `Shop.Services.OrderService`", descriptions);
        Assert.Contains("complete method `Cancel(int)` in `Shop.Services.OrderService`", descriptions);
        Assert.DoesNotContain(descriptions, d => d.Contains("Untouched2"));
    }

    [Fact]
    public async Task Pr_style_ignores_changes_made_on_base_after_branching()
    {
        using var repo = new TempGitRepo();
        repo.Write("a.txt", "one\n");
        repo.Commit("initial");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Write("b.txt", "feature\n");
        repo.Commit("feature work");
        repo.Git("checkout", "-q", "main");
        repo.Write("a.txt", "one changed on main\n");
        repo.Commit("main work");

        var review = await Review(repo);
        var file = Assert.Single(review.Files);
        Assert.Equal("b.txt", file.Path);
        Assert.Equal(["new file, shown in full"], file.Regions[0].Descriptions);
    }

    [Fact]
    public async Task Small_non_csharp_files_are_shown_in_full_and_large_ones_use_enclosing_block()
    {
        using var repo = new TempGitRepo();
        var big = "{\n  \"a\": {\n" + string.Join(",\n", Enumerable.Range(0, 200).Select(i => $"    \"k{i}\": {i}")) + "\n  },\n  \"b\": {\n    \"x\": 1,\n    \"y\": 2\n  }\n}\n";
        repo.Write("small.json", "{\n  \"x\": 1\n}\n");
        repo.Write("big.json", big);
        repo.Commit("initial");
        repo.Git("checkout", "-q", "-b", "feature");
        repo.Write("small.json", "{\n  \"x\": 2\n}\n");
        repo.Write("big.json", big.Replace("\"y\": 2", "\"y\": 3"));
        repo.Commit("change");

        var review = await Review(repo);
        var bigFile = review.Files.Single(f => f.Path == "big.json");
        var region = Assert.Single(bigFile.Regions);
        Assert.Equal(["complete enclosing block `\"b\": {`"], region.Descriptions);
        Assert.Equal(["entire file (small file, shown in full)"], review.Files.Single(f => f.Path == "small.json").Regions[0].Descriptions);
    }

    [Fact]
    public async Task Renames_binary_and_lock_files_are_summarised()
    {
        using var repo = new TempGitRepo();
        repo.Write("old/name.txt", string.Join("\n", Enumerable.Range(0, 20)) + "\n");
        repo.Write("package-lock.json", "{}\n");
        File.WriteAllBytes(Path.Combine(repo.Path, "img.bin"), [0, 1, 2, 0, 3]);
        repo.Commit("initial");
        repo.Git("checkout", "-q", "-b", "feature");
        Directory.CreateDirectory(Path.Combine(repo.Path, "new"));
        repo.Git("mv", "old/name.txt", "new/name.txt");
        repo.Write("package-lock.json", "{\"a\":1}\n");
        File.WriteAllBytes(Path.Combine(repo.Path, "img.bin"), [0, 9, 9, 0, 3]);
        repo.Commit("change");

        var review = await Review(repo);
        Assert.Equal("Renamed without content changes.", review.Files.Single(f => f.Path == "new/name.txt").Note);
        Assert.Equal("old/name.txt", review.Files.Single(f => f.Path == "new/name.txt").File.OldPath);
        Assert.Equal("Binary file; content not shown.", review.Files.Single(f => f.Path == "img.bin").Note);
        Assert.Contains("omitted", review.Files.Single(f => f.Path == "package-lock.json").Note);
    }

    [Fact]
    public async Task Unknown_branch_gives_clear_error()
    {
        using var repo = new TempGitRepo();
        repo.Write("a.txt", "x\n");
        repo.Commit("initial");
        var ex = await Assert.ThrowsAsync<Git.GitException>(() => new ReviewBuilder().BuildAsync(repo.Path, "main", "nope", new ReviewOptions()));
        Assert.Contains("'nope' is not a branch", ex.Message);
    }
}
