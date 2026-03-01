using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HappyEngine.Managers
{
    public class BusMessage
    {
        public string From { get; set; } = "";
        public string FromSummary { get; set; } = "";
        public string Type { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Body { get; set; } = "";
        public List<string> Mentions { get; set; } = new();
        public DateTime Timestamp { get; set; }
        public string SourceFile { get; set; } = "";
    }

    public class BusParticipant
    {
        public string TaskId { get; set; } = "";
        public string Summary { get; set; } = "";
    }

    internal class BusContext
    {
        public string BusDir { get; set; } = "";
        public string InboxDir { get; set; } = "";
        public FileSystemWatcher? Watcher { get; set; }
        public DispatcherTimer? PollTimer { get; set; }
        public HashSet<string> ParticipantTaskIds { get; set; } = new();
        public Dictionary<string, string> TaskSummaries { get; set; } = new();
        public List<BusMessage> Messages { get; set; } = new();
        public HashSet<string> ProcessedFiles { get; set; } = new();
    }

    public class MessageBusManager : IDisposable
    {
        private bool _disposed;
        private readonly Dictionary<string, BusContext> _buses = new();
        private readonly Dispatcher _dispatcher;

        public event Action<string, BusMessage>? MessageReceived;

        public MessageBusManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void JoinBus(string projectPath, string taskId, string taskSummary)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx))
                ctx = CreateBus(projectPath);

            ctx.ParticipantTaskIds.Add(taskId);
            ctx.TaskSummaries[taskId] = taskSummary;
            RebuildScratchpad(projectPath);
            AppLogger.Info("MessageBus", $"Task {taskId} joined bus for {projectPath} ({ctx.ParticipantTaskIds.Count} participants)");
        }

        public void LeaveBus(string projectPath, string taskId)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx)) return;

            ctx.ParticipantTaskIds.Remove(taskId);
            ctx.TaskSummaries.Remove(taskId);
            AppLogger.Info("MessageBus", $"Task {taskId} left bus for {projectPath} ({ctx.ParticipantTaskIds.Count} remaining)");

            if (ctx.ParticipantTaskIds.Count == 0)
                DestroyBus(projectPath);
            else
                RebuildScratchpad(projectPath);
        }

        public List<BusParticipant> GetParticipants(string projectPath)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx))
                return new List<BusParticipant>();

            return ctx.ParticipantTaskIds.Select(id => new BusParticipant
            {
                TaskId = id,
                Summary = ctx.TaskSummaries.GetValueOrDefault(id, "Unknown")
            }).ToList();
        }

        public List<BusMessage> GetRecentMessages(string projectPath, int maxCount = 50)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx))
                return new List<BusMessage>();
            return ctx.Messages.OrderByDescending(m => m.Timestamp).Take(maxCount).ToList();
        }

        private BusContext CreateBus(string projectPath)
        {
            var busDir = Path.Combine(projectPath, ".agent-bus");
            var inboxDir = Path.Combine(busDir, "inbox");
            Directory.CreateDirectory(inboxDir);
            EnsureGitExclude(projectPath);

            var ctx = new BusContext { BusDir = busDir, InboxDir = inboxDir };

            try
            {
                var watcher = new FileSystemWatcher(inboxDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };
                watcher.Created += (_, e) =>
                    _dispatcher.BeginInvoke(async () => await OnNewMessageFileAsync(projectPath, e.FullPath));
                ctx.Watcher = watcher;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MessageBus", $"FileSystemWatcher failed for {inboxDir}, relying on polling", ex);
            }

            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            poll.Tick += (_, _) => PollForNewMessages(projectPath);
            poll.Start();
            ctx.PollTimer = poll;

            _buses[projectPath] = ctx;
            AppLogger.Info("MessageBus", $"Bus created at {busDir}");
            return ctx;
        }

        private void DestroyBus(string projectPath)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx)) return;

            ctx.Watcher?.Dispose();
            ctx.PollTimer?.Stop();

            try
            {
                if (Directory.Exists(ctx.BusDir))
                    Directory.Delete(ctx.BusDir, true);
                AppLogger.Info("MessageBus", $"Bus destroyed for {projectPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MessageBus", $"Failed to delete bus dir for {projectPath}", ex);
            }

            _buses.Remove(projectPath);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var projectPath in _buses.Keys.ToList())
                DestroyBus(projectPath);
        }

        private async Task OnNewMessageFileAsync(string projectPath, string filePath)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx)) return;
            var fileName = Path.GetFileName(filePath);
            if (ctx.ProcessedFiles.Contains(fileName)) return;
            ctx.ProcessedFiles.Add(fileName);

            var message = await ParseMessageFileAsync(filePath);
            if (message == null) return;

            message.FromSummary = ctx.TaskSummaries.GetValueOrDefault(message.From, "Unknown");
            message.SourceFile = fileName;

            ctx.Messages.Add(message);
            RebuildScratchpad(projectPath);
            AppLogger.Info("MessageBus", $"Message from {message.From}: [{message.Type}] {message.Topic}");
            MessageReceived?.Invoke(projectPath, message);
        }

        private async void PollForNewMessages(string projectPath)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx)) return;
            if (!Directory.Exists(ctx.InboxDir)) return;

            try
            {
                foreach (var file in Directory.GetFiles(ctx.InboxDir, "*.json"))
                {
                    var fileName = Path.GetFileName(file);
                    if (ctx.ProcessedFiles.Contains(fileName)) continue;
                    await OnNewMessageFileAsync(projectPath, file);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MessageBus", $"Poll error for {projectPath}", ex);
            }
        }

        private static async Task<BusMessage?> ParseMessageFileAsync(string filePath)
        {
            try
            {
                string json = "";
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        json = await File.ReadAllTextAsync(filePath);
                        if (!string.IsNullOrWhiteSpace(json)) break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(100);
                    }
                }
                if (string.IsNullOrWhiteSpace(json)) return null;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new BusMessage
                {
                    From = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "",
                    Type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    Topic = root.TryGetProperty("topic", out var tp) ? tp.GetString() ?? "" : "",
                    Body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    Mentions = root.TryGetProperty("mentions", out var m) && m.ValueKind == JsonValueKind.Array
                        ? m.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                        : new List<string>(),
                    Timestamp = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MessageBus", $"Failed to parse message file {filePath}", ex);
                return null;
            }
        }

        private void RebuildScratchpad(string projectPath)
        {
            if (!_buses.TryGetValue(projectPath, out var ctx)) return;

            var sb = new StringBuilder();
            sb.AppendLine("# Agent Message Bus - Scratchpad");
            sb.AppendLine($"Last updated: {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine();

            sb.AppendLine("## Active Sibling Tasks");
            sb.AppendLine("| ID | Summary |");
            sb.AppendLine("|----|---------|");
            foreach (var taskId in ctx.ParticipantTaskIds)
            {
                var summary = ctx.TaskSummaries.GetValueOrDefault(taskId, "Unknown");
                sb.AppendLine($"| {taskId} | {summary} |");
            }
            sb.AppendLine();

            var recent = ctx.Messages.OrderByDescending(m => m.Timestamp).Take(50).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("## Recent Messages (newest first)");
                foreach (var msg in recent)
                {
                    sb.AppendLine($"### [{msg.Timestamp:HH:mm}] {msg.From} ({msg.FromSummary}) -- {msg.Type.ToUpperInvariant()}");
                    if (!string.IsNullOrEmpty(msg.Topic))
                        sb.AppendLine($"**Topic:** {msg.Topic}");
                    sb.AppendLine(msg.Body);
                    sb.AppendLine();
                }
            }

            var claims = ctx.Messages
                .Where(m => string.Equals(m.Type, "claim", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.From)
                .ToList();
            if (claims.Count > 0)
            {
                sb.AppendLine("## Claims");
                foreach (var group in claims)
                {
                    var name = ctx.TaskSummaries.GetValueOrDefault(group.Key, group.Key);
                    foreach (var claim in group)
                        sb.AppendLine($"- {name}: {claim.Body}");
                }
            }

            try
            {
                var scratchpadPath = Path.Combine(ctx.BusDir, "_scratchpad.md");
                File.WriteAllText(scratchpadPath, sb.ToString());
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MessageBus", "Failed to write scratchpad", ex);
            }
        }

        private static void EnsureGitExclude(string projectPath)
        {
            var gitDir = Path.Combine(projectPath, ".git");
            if (!Directory.Exists(gitDir)) return;

            try
            {
                var excludeDir = Path.Combine(gitDir, "info");
                Directory.CreateDirectory(excludeDir);
                var excludeFile = Path.Combine(excludeDir, "exclude");

                var content = File.Exists(excludeFile) ? File.ReadAllText(excludeFile) : "";
                if (!content.Contains(".agent-bus"))
                    File.AppendAllText(excludeFile, "\n.agent-bus/\n");
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MessageBus", "Failed to update .git/info/exclude", ex);
            }
        }
    }
}
