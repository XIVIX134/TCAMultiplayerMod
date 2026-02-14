using System;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TCAMultiplayer
{
    /// <summary>
    /// Instance-specific logger that writes to separate log files for Host/Client.
    /// Produces clean, readable logs with minimal noise.
    /// </summary>
    public static class InstanceLogger
    {
        private static string _logDirectory;
        private static string _logFilePath;
        private static StreamWriter _writer;
        private static readonly object _lock = new object();
        private static bool _initialized = false;
        private static bool _instanceTypeSet = false;
        private static string _instanceType = "Unknown";
        private static int _sessionId;
        
        // Throttling for repetitive logs
        private static string _lastLogMessage = "";
        private static int _repeatCount = 0;
        
        /// <summary>
        /// The instance type string used for log prefixes (e.g., "HOST", "CLIENT")
        /// </summary>
        public static string InstanceType => _instanceType;
        
        /// <summary>
        /// Full path to the current log file
        /// </summary>
        public static string LogFilePath => _logFilePath;
        
        /// <summary>
        /// Whether the logger has been initialized
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// Initialize the instance logger with a unique session ID.
        /// Called early in Plugin.Awake() before we know if host or client.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            try
            {
                // Generate unique session ID using process ID and timestamp
                _sessionId = System.Diagnostics.Process.GetCurrentProcess().Id;
                
                // Create log directory
                _logDirectory = Path.Combine(
                    Path.GetDirectoryName(typeof(Plugin).Assembly.Location),
                    "..", // Go up from plugins to BepInEx
                    "TCAMultiplayer"
                );
                
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }
                
                // Delete old log files from previous sessions
                CleanupOldLogs();
                
                // Create initial log file with session ID (will rename when host/client is known)
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = $"Session_{_sessionId}_{timestamp}.log";
                _logFilePath = Path.Combine(_logDirectory, fileName);
                
                // Open writer with auto-flush
                _writer = new StreamWriter(_logFilePath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                
                _initialized = true;
                
                // Write header
                WriteHeader();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[InstanceLogger] Failed to initialize: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the instance type (host or client). Called when GameStateMachine transitions.
        /// Renames the log file to include the instance type.
        /// </summary>
        /// <param name="isHost">True if this instance is the host</param>
        public static void SetInstanceType(bool isHost)
        {
            if (!_initialized) return;
            
            string newInstanceType = isHost ? "HOST" : "CLIENT";
            
            lock (_lock)
            {
                // Log the transition
                WriteLine("");
                WriteSectionHeader($"INSTANCE TYPE: {newInstanceType}");
                WriteLine("");
                
                _instanceType = newInstanceType;
                _instanceTypeSet = true;
                
                // Rename the log file to include instance type
                try
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string newFileName = $"{_instanceType}_{timestamp}.log";
                    string newPath = Path.Combine(_logDirectory, newFileName);
                    
                    // Close current writer
                    _writer?.Close();
                    _writer?.Dispose();
                    
                    // Rename the file
                    if (File.Exists(_logFilePath))
                    {
                        File.Move(_logFilePath, newPath);
                        _logFilePath = newPath;
                    }
                    
                    // Reopen writer in append mode
                    _writer = new StreamWriter(_logFilePath, append: true, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[InstanceLogger] Could not rename log file: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Delete all old TCA log files from previous sessions
        /// </summary>
        private static void CleanupOldLogs()
        {
            try
            {
                if (!Directory.Exists(_logDirectory)) return;
                
                string[] oldLogs = Directory.GetFiles(_logDirectory, "*.log");
                foreach (string logFile in oldLogs)
                {
                    try
                    {
                        File.Delete(logFile);
                    }
                    catch
                    {
                        // Ignore deletion errors
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// Write the log file header with session info
        /// </summary>
        private static void WriteHeader()
        {
            WriteLine("╔══════════════════════════════════════════════════════════════╗");
            WriteLine("║              TCA MULTIPLAYER - DEBUG LOG                     ║");
            WriteLine("╚══════════════════════════════════════════════════════════════╝");
            WriteLine($"");
            WriteLine($"  Version:    {PluginInfo.VERSION}");
            WriteLine($"  Process ID: {_sessionId}");
            WriteLine($"  Started:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine($"");
            WriteLine($"  Instance type will be set when hosting or connecting...");
            WriteLine($"");
            WriteLine("────────────────────────────────────────────────────────────────");
        }

        /// <summary>
        /// Write a section header for visual separation
        /// </summary>
        private static void WriteSectionHeader(string title)
        {
            WriteLine("");
            WriteLine($"┌─ {title} ─────────────────────────────────────────┐");
            WriteLine("");
        }

        /// <summary>
        /// Log an info message with clean formatting
        /// </summary>
        public static void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// Log a warning message with clean formatting
        /// </summary>
        public static void LogWarning(string message)
        {
            WriteLog("WARN", message, " ⚠ ");
        }

        /// <summary>
        /// Log an error message with clean formatting
        /// </summary>
        public static void LogError(string message)
        {
            WriteLog("ERROR", message, " ❌ ");
        }

        /// <summary>
        /// Log a debug message (only in debug builds)
        /// </summary>
        public static void LogDebug(string message)
        {
            #if DEBUG
            WriteLog("DEBUG", message, " 🔍 ");
            #endif
        }

        /// <summary>
        /// Log a network message with special formatting
        /// </summary>
        public static void LogNetwork(string direction, string details)
        {
            string arrow = direction == "SEND" ? "→" : "←";
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string formattedMessage = $"[{timestamp}] {arrow} {details}";
            
            WriteLine(formattedMessage);
        }

        /// <summary>
        /// Log a state transition with visual emphasis
        /// </summary>
        public static void LogStateChange(string fromState, string toState)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            WriteLine($"[{timestamp}] 🔄 STATE: {fromState} → {toState}");
        }

        /// <summary>
        /// Write a formatted log entry with clean formatting
        /// </summary>
        private static void WriteLog(string level, string message, string prefix = "")
        {
            if (!_initialized || _writer == null) return;
            
            // Skip very noisy repetitive logs
            if (IsNoisyLog(message))
            {
                // Just count repeats, don't spam
                if (message == _lastLogMessage)
                {
                    _repeatCount++;
                    return;
                }
                
                // If we had repeats, summarize them
                if (_repeatCount > 0)
                {
                    string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    WriteLine($"[{timestamp}] ... (repeated {_repeatCount}x)");
                    _repeatCount = 0;
                }
                
                _lastLogMessage = message;
            }
            
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            
            // Clean up the message - remove redundant prefixes
            message = CleanMessage(message);
            
            // Format based on level
            string levelStr = level.PadRight(5);
            string formattedMessage = $"[{time}] {prefix}{message}";
            
            WriteLine(formattedMessage);
        }

        /// <summary>
        /// Check if a log message is noisy/repetitive and should be throttled
        /// </summary>
        private static bool IsNoisyLog(string message)
        {
            return message.Contains("Drawing lobby") ||
                   message.Contains("Updates:") ||
                   message.Contains("ACK received") ||
                   message.Contains("Field:") ||
                   message.Contains("Property:");
        }

        /// <summary>
        /// Clean up message by removing redundant prefixes
        /// </summary>
        private static string CleanMessage(string message)
        {
            // Remove common redundant prefixes
            message = message.Replace("[TCA Multiplayer] ", "");
            message = message.Replace("[Info   :TCA Multiplayer] ", "");
            message = message.Replace("[Warning :TCA Multiplayer] ", "");
            message = message.Replace("[Error   :TCA Multiplayer] ", "");
            
            return message;
        }

        /// <summary>
        /// Write a line to the log file (thread-safe)
        /// </summary>
        private static void WriteLine(string line)
        {
            if (!_initialized || _writer == null) return;
            
            lock (_lock)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch
                {
                    // Ignore write errors
                }
            }
        }

        /// <summary>
        /// Get the instance prefix for BepInEx logs (e.g., "[HOST] ")
        /// </summary>
        public static string GetPrefix()
        {
            return _instanceTypeSet ? $"[{_instanceType}] " : "";
        }

        /// <summary>
        /// Shutdown the logger and close the file
        /// </summary>
        public static void Shutdown()
        {
            if (!_initialized) return;
            
            lock (_lock)
            {
                try
                {
                    // Write any pending repeat count
                    if (_repeatCount > 0)
                    {
                        WriteLine($"... (repeated {_repeatCount}x)");
                    }
                    
                    WriteLine("");
                    WriteLine("────────────────────────────────────────────────────────────────");
                    WriteLine($"");
                    WriteLine($"  Session ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    WriteLine($"");
                    WriteLine("╚══════════════════════════════════════════════════════════════╝");
                    
                    _writer?.Close();
                    _writer?.Dispose();
                    _writer = null;
                }
                catch
                {
                    // Ignore shutdown errors
                }
            }
            
            _initialized = false;
        }
    }
}
