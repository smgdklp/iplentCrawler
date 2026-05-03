using Microsoft.Playwright;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    // 识别结果数据模型（增强版）
    public class RecognitionResult
    {
        public List<(string Name, double Score)> Families { get; set; } = new();
        public List<(string Name, double Score)> Genera { get; set; } = new();
        public List<(string Name, double Score)> Species { get; set; } = new();
        
        // 新增字段
        public bool Valid { get; set; } = true;
        public string Wrong { get; set; } = "";

        public override string ToString()
        {
            if (!Valid)
                return $"❌ 无效: {Wrong}";
            
            return $"科: {string.Join(", ", Families.Select(f => $"{f.Name}({f.Score:F2}%)"))}\n" +
                   $"属: {string.Join(", ", Genera.Select(g => $"{g.Name}({g.Score:F2}%)"))}\n" +
                   $"种: {string.Join(", ", Species.Select(s => $"{s.Name}({s.Score:F2}%)"))}";
        }
        
        // 转为JSON对象（用于序列化）
        public object ToJsonObject()
        {
            return new
            {
                valid = Valid,
                wrong = Wrong,
                families = Families.Select(f => new { name = f.Name, score = f.Score }),
                genera = Genera.Select(g => new { name = g.Name, score = g.Score }),
                species = Species.Select(s => new { name = s.Name, score = s.Score }),
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
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
    public async Task<bool> Check()
    {
        if (page == null)
        {
            Console.WriteLine("[Check] 浏览器未初始化");
            return false;
        }
        try
        {
            string title = await page.TitleAsync();
            Console.WriteLine($"[Check] 浏览器正常，标题: {title}\n");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Check] 浏览器异常: {ex.Message}");
            return false;
        }
    }

    // 提取所有识别结果（科、属、种及置信度）
    private async Task<RecognitionResult> ExtractAllResults()
    {
        var result = new RecognitionResult();

        if (page == null) return result;

        try
        {
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
            
            // 如果三个列表都为空，说明识别失败
            if (result.Families.Count == 0 && result.Genera.Count == 0 && result.Species.Count == 0)
            {
                result.Valid = false;
                result.Wrong = "未识别出任何植物（可能是图片质量问题或AI无法识别）";
            }
        }
        catch (Exception ex)
        {
            result.Valid = false;
            result.Wrong = $"提取结果时异常: {ex.Message}";
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

    // 判断文件是否为支持的图片格式
    private bool IsValidImageExtension(string filePath)
    {
        string[] validExtensions = { ".jpg", ".jpeg", ".png", ".pjpeg", ".jfif" };
        string ext = Path.GetExtension(filePath).ToLower();
        return validExtensions.Contains(ext);
    }

    // 接受图片完成检测
    public async Task Reco(string newPath)
    {
        Console.WriteLine($"[Reco] 开始识别: {Path.GetFileName(newPath)}");

        if (!File.Exists(newPath))
        {
            Console.WriteLine($"[Reco] 文件不存在: {newPath}");
            LastResult = new RecognitionResult { Valid = false, Wrong = "文件不存在" };
            return;
        }
        
        if (page == null)
        {
            Console.WriteLine("[Reco] 浏览器未初始化");
            LastResult = new RecognitionResult { Valid = false, Wrong = "浏览器未初始化" };
            return;
        }

        // 检查文件扩展名
        if (!IsValidImageExtension(newPath))
        {
            string ext = Path.GetExtension(newPath);
            LastResult = new RecognitionResult { Valid = false, Wrong = $"后缀错了（不支持{ext}，仅支持.jpg/.jpeg/.png/.pjpeg/.jfif）" };
            Console.WriteLine($"[Reco] {LastResult.Wrong}");
            return;
        }

        try
        {
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Reco] 识别失败: {ex.Message}");
            LastResult = new RecognitionResult { Valid = false, Wrong = $"识别过程异常: {ex.Message}" };
        }
    }

    // 保存结果（支持txt和json）
    public async Task SaveResult(string imagePath, string customSavePath = null)
    {
        string targetPath = customSavePath ?? savePath;
        
        try
        {
            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            
            string extension = Path.GetExtension(targetPath).ToLower();
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            
            if (extension == ".json")
            {
                // JSON格式保存
                await SaveAsJson(targetPath, imagePath, timestamp);
            }
            else
            {
                // TXT格式保存（默认）
                await SaveAsTxt(targetPath, imagePath, timestamp);
            }
            
            Console.WriteLine($"[Save] 结果已保存到: {targetPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Save] 保存失败: {ex.Message}");
        }
    }
    
    private async Task SaveAsTxt(string filePath, string imagePath, string timestamp)
    {
        var lines = new List<string>
        {
            $"\n========== {timestamp} ==========",
            $"图片: {imagePath}",
            $"有效性: {(LastResult.Valid ? "✅ 有效" : "❌ 无效")}"
        };
        
        if (LastResult.Valid)
        {
            lines.Add("=== 科 ===");
            foreach (var fam in LastResult.Families)
                lines.Add($"  {fam.Name} ({fam.Score:F2}%)");
            
            lines.Add("=== 属 ===");
            foreach (var gen in LastResult.Genera)
                lines.Add($"  {gen.Name} ({gen.Score:F2}%)");
            
            lines.Add("=== 种 ===");
            foreach (var sp in LastResult.Species)
                lines.Add($"  {sp.Name} ({sp.Score:F2}%)");
        }
        else
        {
            lines.Add($"错误类型: {LastResult.Wrong}");
        }
        
        lines.Add("");
        await File.AppendAllLinesAsync(filePath, lines, Encoding.UTF8);
    }
    
    private async Task SaveAsJson(string filePath, string imagePath, string timestamp)
    {
        var record = new
        {
            timestamp = timestamp,
            image = imagePath,
            valid = LastResult.Valid,
            wrong = LastResult.Wrong,
            families = LastResult.Families.Select(f => new { name = f.Name, score = f.Score }),
            genera = LastResult.Genera.Select(g => new { name = g.Name, score = g.Score }),
            species = LastResult.Species.Select(s => new { name = s.Name, score = s.Score })
        };
        
        string jsonLine = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = false });
        
        // JSON Lines 格式（每行一个JSON对象，便于追加和分析）
        await File.AppendAllLinesAsync(filePath, new[] { jsonLine }, Encoding.UTF8);
    }

    // 确保保存路径的文件存在（如果是.json则创建空文件，如果是.txt则创建空文件）
    private void EnsureSaveFileExists(string savePath)
    {
        string directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        
        if (!File.Exists(savePath))
        {
            string extension = Path.GetExtension(savePath).ToLower();
            if (extension == ".json")
            {
                // 创建空JSON文件（写入空数组作为起始，或者直接空文件）
                File.WriteAllText(savePath, "", Encoding.UTF8);
            }
            else
            {
                // 创建空TXT文件，写入表头
                File.WriteAllText(savePath, "植物识别结果记录\n生成时间: " + DateTime.Now + "\n" + new string('=', 50) + "\n", Encoding.UTF8);
            }
            Console.WriteLine($"[Init] 已创建保存文件: {savePath}");
        }
    }

    // 主要工作方法
    public async Task Work(string sourcePath, string targetSavePath)
    {
        Console.WriteLine($"\n[Work] 开始处理...");
        Console.WriteLine($"  图源路径: {sourcePath}");
        Console.WriteLine($"  保存路径: {targetSavePath}");
        
        // 检查浏览器是否初始化
        bool isBrowserReady = await Check();
        if (!isBrowserReady)
        {
            Console.WriteLine("[Work] 浏览器未就绪，尝试初始化...");
            await Initweb();
            isBrowserReady = await Check();
            if (!isBrowserReady)
            {
                Console.WriteLine("[Work] ❌ 浏览器初始化失败，无法继续");
                return;
            }
        }
        
        // 确保保存路径文件存在
        EnsureSaveFileExists(targetSavePath);
        
        // 检测sourcePath是文件夹还是单文件
        bool isDirectory = Directory.Exists(sourcePath);
        bool isFile = File.Exists(sourcePath);
        
        if (!isDirectory && !isFile)
        {
            Console.WriteLine($"[Work] ❌ 路径无效: {sourcePath}");
            return;
        }
        
        if (isFile)
        {
            // 单图处理
            Console.WriteLine("\n[Work] 检测到单张图片");
            await Reco(sourcePath);
            await SaveResult(sourcePath, targetSavePath);
        }
        else if (isDirectory)
        {
            // 文件夹批量处理
            Console.WriteLine("\n[Work] 检测到文件夹，开始批量处理...");
            var imageFiles = Directory.GetFiles(sourcePath)
                .Where(f => IsValidImageExtension(f))
                .ToList();
            
            var invalidFiles = Directory.GetFiles(sourcePath)
                .Where(f => !IsValidImageExtension(f))
                .ToList();
            
            Console.WriteLine($"  有效图片: {imageFiles.Count} 张");
            Console.WriteLine($"  无效文件: {invalidFiles.Count} 个（已跳过）");
            
            // 记录无效文件
            if (invalidFiles.Any())
            {
                foreach (var invalidFile in invalidFiles)
                {
                    LastResult = new RecognitionResult { Valid = false, Wrong = $"后缀错了（{Path.GetExtension(invalidFile)}）" };
                    await SaveResult(invalidFile, targetSavePath);
                }
            }
            
            // 处理有效图片
            for (int i = 0; i < imageFiles.Count; i++)
            {
                Console.WriteLine($"\n[进度] ({i+1}/{imageFiles.Count})");
                await Reco(imageFiles[i]);
                await SaveResult(imageFiles[i], targetSavePath);
            }
        }
        
        Console.WriteLine($"\n[Work] ✅ 处理完成！结果保存至: {targetSavePath}");
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
