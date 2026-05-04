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
    public string savePath = @"C:\myc\test\result.json";
    public string web = "https://www.iplant.cn/stu/20k";
    public string logPath = null;  // 日志路径，为null时输出到控制台
    
    private IBrowser browser;
    private IPage page;
    private IPlaywright playwright;

    // 识别结果数据模型（改为struct）
    public struct RecognitionResult
    {
        public List<(string Name, double Score)> Families { get; set; }
        public List<(string Name, double Score)> Genera { get; set; }
        public List<(string Name, double Score)> Species { get; set; }
        public bool Valid { get; set; }
        public string Wrong { get; set; }

        // 构造函数
        public RecognitionResult(bool valid, string wrong = "")
        {
            Families = new List<(string, double)>();
            Genera = new List<(string, double)>();
            Species = new List<(string, double)>();
            Valid = valid;
            Wrong = wrong;
        }

        public override string ToString()
        {
            if (!Valid)
                return $"❌ 无效: {Wrong}";
            
            return $"科: {string.Join(", ", Families.Select(f => $"{f.Name}({f.Score:F2}%)"))}\n" +
                   $"属: {string.Join(", ", Genera.Select(g => $"{g.Name}({g.Score:F2}%)"))}\n" +
                   $"种: {string.Join(", ", Species.Select(s => $"{s.Name}({s.Score:F2}%)"))}";
        }
        
        // 转为JSON对象（用于序列化）
        public object ToJsonObject(string imagePath)
        {
            return new
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                image = imagePath,
                valid = Valid,
                wrong = Wrong,
                families = Families.Select(f => new { name = f.Name, score = f.Score }),
                genera = Genera.Select(g => new { name = g.Name, score = g.Score }),
                species = Species.Select(s => new { name = s.Name, score = s.Score })
            };
        }
    }

    // 设置日志路径
    public void LogIt(string path)
    {
        logPath = path;
        // 确保日志目录存在
        string directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    // 日志写入（如果logPath有效则写文件，否则写控制台）
    private void WriteLog(string message, bool isError = false)
    {
        string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
        
        if (!string.IsNullOrEmpty(logPath))
        {
            try
            {
                File.AppendAllText(logPath, logMessage + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* 日志写入失败静默处理 */ }
        }
        else
        {
            if (isError)
                Console.Error.WriteLine(logMessage);
            else
                Console.WriteLine(logMessage);
        }
    }

    // 初始化浏览器
    public async Task Initweb()
    {
        WriteLog("[Init] 启动浏览器...");
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Channel = "msedge"
        });
        page = await browser.NewPageAsync();
        WriteLog("[Init] 加载网页...");
        await page.GotoAsync(web, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        WriteLog("[Init] 就绪\n");
        
        // 确保保存路径的目录存在
        string saveDir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(saveDir) && !Directory.Exists(saveDir))
            Directory.CreateDirectory(saveDir);
        
        // 如果JSON文件不存在，创建空文件
        if (!File.Exists(savePath))
        {
            File.WriteAllText(savePath, "", Encoding.UTF8);
            WriteLog($"[Init] 已创建保存文件: {savePath}");
        }
    }

    // 检查浏览器状态
    public async Task<bool> Check()
    {
        if (page == null)
        {
            WriteLog("[Check] 浏览器未初始化", true);
            return false;
        }
        try
        {
            string title = await page.TitleAsync();
            WriteLog($"[Check] 浏览器正常，标题: {title}");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"[Check] 浏览器异常: {ex.Message}", true);
            return false;
        }
    }

    // 提取所有识别结果（科、属、种及置信度）
    private async Task<RecognitionResult> ExtractAllResults()
    {
        var result = new RecognitionResult(true);

        if (page == null) return new RecognitionResult(false, "页面未初始化");

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

    // 接受图片完成检测（返回新的结果对象，不缓存）
    public async Task<RecognitionResult> Reco(string newPath)
    {
        WriteLog($"[Reco] 开始识别: {Path.GetFileName(newPath)}");

        if (!File.Exists(newPath))
        {
            WriteLog($"[Reco] 文件不存在: {newPath}", true);
            return new RecognitionResult(false, "文件不存在");
        }
        
        if (page == null)
        {
            WriteLog("[Reco] 浏览器未初始化", true);
            return new RecognitionResult(false, "浏览器未初始化");
        }

        // 检查文件扩展名
        if (!IsValidImageExtension(newPath))
        {
            string ext = Path.GetExtension(newPath);
            string error = $"后缀错了（不支持{ext}，仅支持.jpg/.jpeg/.png/.pjpeg/.jfif）";
            WriteLog($"[Reco] {error}", true);
            return new RecognitionResult(false, error);
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
            WriteLog("[Reco] 等待AI识别...");
            await page.WaitForFunctionAsync(@"
                () => {
                    const fam = document.querySelector('.stugroup-item-fam');
                    const gen = document.querySelector('.stugroup-item-gen');
                    const species = document.querySelector('.stugroup-item:not(.stugroup-item-fam):not(.stugroup-item-gen)');
                    return fam !== null || gen !== null || species !== null;
                }
            ", new PageWaitForFunctionOptions { Timeout = 60000 });

            // 提取所有识别结果
            var result = await ExtractAllResults();
            WriteLog($"[Reco] 识别成功!");
            WriteLog(result.ToString());
            return result;
        }
        catch (Exception ex)
        {
            WriteLog($"[Reco] 识别失败: {ex.Message}", true);
            return new RecognitionResult(false, $"识别过程异常: {ex.Message}");
        }
    }

    // 保存结果到JSON（直接追加，不格式化）
    public async Task SaveResult(string imagePath, RecognitionResult result)
    {
        try
        {
            // 确保目录存在
            string directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            
            // 生成JSON行（不格式化，直接一行）
            var jsonObj = result.ToJsonObject(imagePath);
            string jsonLine = JsonSerializer.Serialize(jsonObj, new JsonSerializerOptions { WriteIndented = false });
            
            // 追加到文件
            await File.AppendAllLinesAsync(savePath, new[] { jsonLine }, Encoding.UTF8);
            
            WriteLog($"[Save] 结果已保存到: {savePath}");
        }
        catch (Exception ex)
        {
            WriteLog($"[Save] 保存失败: {ex.Message}", true);
        }
    }

    // 主要工作方法
    public async Task Work(string sourcePath, string targetSavePath, string targetLogPath = null)
    {
        // 设置保存路径
        if (!string.IsNullOrEmpty(targetSavePath))
            savePath = targetSavePath;
        
        // 设置日志路径
        if (!string.IsNullOrEmpty(targetLogPath))
            LogIt(targetLogPath);
        
        WriteLog($"\n[Work] 开始处理...");
        WriteLog($"  图源路径: {sourcePath}");
        WriteLog($"  保存路径: {savePath}");
        WriteLog($"  日志路径: {logPath ?? "(控制台)"}");
        
        // 检查浏览器是否初始化
        bool isBrowserReady = await Check();
        if (!isBrowserReady)
        {
            WriteLog("[Work] 浏览器未就绪，尝试初始化...");
            await Initweb();
            isBrowserReady = await Check();
            if (!isBrowserReady)
            {
                WriteLog("[Work] ❌ 浏览器初始化失败，无法继续", true);
                return;
            }
        }
        
        // 检测sourcePath是文件夹还是单文件
        bool isDirectory = Directory.Exists(sourcePath);
        bool isFile = File.Exists(sourcePath);
        
        if (!isDirectory && !isFile)
        {
            WriteLog($"[Work] ❌ 路径无效: {sourcePath}", true);
            return;
        }
        
        if (isFile)
        {
            // 单图处理
            WriteLog("\n[Work] 检测到单张图片");
            var result = await Reco(sourcePath);
            await SaveResult(sourcePath, result);
        }
        else if (isDirectory)
        {
            // 文件夹批量处理
            WriteLog("\n[Work] 检测到文件夹，开始批量处理...");
            var imageFiles = Directory.GetFiles(sourcePath)
                .Where(f => IsValidImageExtension(f))
                .ToList();
            
            var invalidFiles = Directory.GetFiles(sourcePath)
                .Where(f => !IsValidImageExtension(f))
                .ToList();
            
            WriteLog($"  有效图片: {imageFiles.Count} 张");
            WriteLog($"  无效文件: {invalidFiles.Count} 个（已跳过）");
            
            // 记录无效文件
            if (invalidFiles.Any())
            {
                foreach (var invalidFile in invalidFiles)
                {
                    var result = new RecognitionResult(false, $"后缀错了（{Path.GetExtension(invalidFile)}）");
                    await SaveResult(invalidFile, result);
                }
            }
            
            // 处理有效图片
            for (int i = 0; i < imageFiles.Count; i++)
            {
                WriteLog($"\n[进度] ({i+1}/{imageFiles.Count})");
                var result = await Reco(imageFiles[i]);
                await SaveResult(imageFiles[i], result);
            }
        }
        
        WriteLog($"\n[Work] ✅ 处理完成！结果保存至: {savePath}");
    }
    
    // 关闭浏览器
    public async Task Closewed()
    {
        if (browser != null) await browser.CloseAsync();
        playwright?.Dispose();
        WriteLog("[Close] 浏览器已关闭");
    }
}

