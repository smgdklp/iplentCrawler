using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Crawler
{
    public string savePath = @"C:\myc\test\result.txt";
    public string web = "https://www.iplant.cn/stu/20k";
    public RecognitionResult LastResult { get; private set; } = new();

    private IBrowser browser;
    private IPage page;
    private IPlaywright playwright;

    // 识别结果数据模型
    public class RecognitionResult
    {
        public List<(string Name, double Score)> Families { get; set; } = new();
        public List<(string Name, double Score)> Genera { get; set; } = new();
        public List<(string Name, double Score)> Species { get; set; } = new();

        public override string ToString()
        {
            return $"科: {string.Join(", ", Families.Select(f => $"{f.Name}({f.Score:F2}%)"))}\n" +
                   $"属: {string.Join(", ", Genera.Select(g => $"{g.Name}({g.Score:F2}%)"))}\n" +
                   $"种: {string.Join(", ", Species.Select(s => $"{s.Name}({s.Score:F2}%)"))}";
        }
    }

    // 初始化浏览器
    public async Task Initweb()
    {
        Console.WriteLine("[Init] 启动浏览器...");
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = "msedge"
        });
        page = await browser.NewPageAsync();
        Console.WriteLine("[Init] 加载网页...");
        await page.GotoAsync(web, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Console.WriteLine("[Init] 就绪\n");
    }

    // 检查浏览器状态
    public async Task Check()
    {
        if (page == null)
        {
            Console.WriteLine("[Check] 浏览器未初始化");
            return;
        }
        try
        {
            string title = await page.TitleAsync();
            Console.WriteLine($"[Check] 浏览器正常，标题: {title}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Check] 浏览器异常: {ex.Message}");
        }
    }

    // 提取所有识别结果（科、属、种及置信度）
    private async Task<RecognitionResult> ExtractAllResults()
    {
        var result = new RecognitionResult();

        if (page == null) return result;

        // 提取科的结果（stugroup-item-fam）
        var famItems = page.Locator(".stugroup-item-fam .stugroup-label");
        int famCount = await famItems.CountAsync();
        for (int i = 0; i < famCount; i++)
        {
            var item = famItems.Nth(i);
            string name = (await item.GetAttributeAsync("data-plant-name")) ?? "";
            string scoreText = await item.Locator(".stugroup-score").TextContentAsync() ?? "0%";
            double score = ParseScore(scoreText);
            if (!string.IsNullOrEmpty(name))
                result.Families.Add((name, score));
        }

        // 提取属的结果（stugroup-item-gen）
        var genItems = page.Locator(".stugroup-item-gen .stugroup-label");
        int genCount = await genItems.CountAsync();
        for (int i = 0; i < genCount; i++)
        {
            var item = genItems.Nth(i);
            string name = (await item.GetAttributeAsync("data-plant-name")) ?? "";
            string scoreText = await item.Locator(".stugroup-score").TextContentAsync() ?? "0%";
            double score = ParseScore(scoreText);
            if (!string.IsNullOrEmpty(name))
                result.Genera.Add((name, score));
        }

        // 提取物种的结果（stugroup-item 但不包含 fam/gen 类）
        var speciesItems = page.Locator(".stugroup-item:not(.stugroup-item-fam):not(.stugroup-item-gen) .stugroup-label");
        int speciesCount = await speciesItems.CountAsync();
        for (int i = 0; i < speciesCount; i++)
        {
            var item = speciesItems.Nth(i);
            string name = (await item.GetAttributeAsync("data-plant-name")) ?? "";
            string scoreText = await item.Locator(".stugroup-score").TextContentAsync() ?? "0%";
            double score = ParseScore(scoreText);
            if (!string.IsNullOrEmpty(name))
                result.Species.Add((name, score));
        }

        return result;
    }

    // 解析百分比字符串 "99.18%" -> 99.18
    private double ParseScore(string scoreText)
    {
        var match = Regex.Match(scoreText, @"(\d+(?:\.\d+)?)");
        if (match.Success && double.TryParse(match.Groups[1].Value, out double value))
            return value;
        return 0;
    }

    // 接受图片完成检测
    public async Task Reco(string newPath)
    {
        Console.WriteLine($"[Reco] 开始识别: {Path.GetFileName(newPath)}");

        if (!File.Exists(newPath))
        {
            Console.WriteLine($"[Reco] 文件不存在: {newPath}");
            return;
        }
        if (page == null)
        {
            Console.WriteLine("[Reco] 浏览器未初始化");
            return;
        }

        try
        {
            // 获取旧结果（用于对比变化）
            var resultLocator = page.Locator("div.infocname");
            string oldResult = null;
            try { oldResult = await resultLocator.TextContentAsync(); } catch { }
            Console.WriteLine($"[Reco] 上传前结果: {oldResult ?? "无"}");

            // 上传图片
            await page.Locator("input[type='file']").SetInputFilesAsync(newPath);

            // 等待上传完成
            var uploading = page.Locator("text=正在上传");
            if (await uploading.CountAsync() > 0)
                await uploading.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30000 });

            // 等待结果变化（等待结果区域出现数据）
            Console.WriteLine("[Reco] 等待AI识别...");
            await page.WaitForFunctionAsync(@"
                () => {
                    const fam = document.querySelector('.stugroup-item-fam');
                    const gen = document.querySelector('.stugroup-item-gen');
                    const species = document.querySelector('.stugroup-item:not(.stugroup-item-fam):not(.stugroup-item-gen)');
                    return fam !== null || gen !== null || species !== null;
                }
            ", new PageWaitForFunctionOptions { Timeout = 60000 });

            // 提取所有识别结果
            LastResult = await ExtractAllResults();
            Console.WriteLine($"[Reco] 识别成功!");
            Console.WriteLine(LastResult.ToString());

            // 缓存结果
            await SaveResult(newPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Reco] 识别失败: {ex.Message}");
        }
    }

    // 缓存结果到文件
    public async Task SaveResult(string imagePath)
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var lines = new List<string>
            {
                $"\n========== {timestamp} ==========",
                $"图片: {imagePath}",
                "=== 科 ==="
            };

            foreach (var fam in LastResult.Families)
                lines.Add($"  {fam.Name} ({fam.Score:F2}%)");

            lines.Add("=== 属 ===");
            foreach (var gen in LastResult.Genera)
                lines.Add($"  {gen.Name} ({gen.Score:F2}%)");

            lines.Add("=== 种 ===");
            foreach (var sp in LastResult.Species)
                lines.Add($"  {sp.Name} ({sp.Score:F2}%)");

            lines.Add("");

            await File.AppendAllLinesAsync(savePath, lines);
            Console.WriteLine($"[Save] 结果已保存到: {savePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Save] 保存失败: {ex.Message}");
        }
    }

    // 关闭浏览器
    public async Task Closewed()
    {
        if (browser != null) await browser.CloseAsync();
        playwright?.Dispose();
        Console.WriteLine("[Close] 浏览器已关闭");
    }
}

// 测试代码
public class Program
{
    public static async Task Main()
    {
        Console.WriteLine("=== 植物识别爬虫 (多结果版) ===\n");

        var crawler = new Crawler();

        try
        {
            await crawler.Initweb();
            await crawler.Check();

            // 可以测试多张图片
            string[] testImages = new[]
            {
                @"C:\mypy\recog_ai\imgs\flaw\result\round_0002_edge_mask.jpg"
                // 可以添加更多图片路径
            };

            foreach (var image in testImages)
            {
                await crawler.Reco(image);
                Console.WriteLine(); // 空行分隔
            }

            Console.WriteLine($"\n🎉 所有识别完成！结果保存在: {crawler.savePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"💀 异常: {ex.Message}");
        }
        finally
        {
            await crawler.Closewed();
        }

        Console.WriteLine("\n按任意键退出...");
        Console.ReadKey();
    }
}