using System;
using System.IO;

namespace BuhWise.Services
{
    public static class AppPaths
    {
        /// <summary>
        /// Application data directory: %LOCALAPPDATA%\BuhWise
        /// </summary>
        public static string AppDataDirectory
        {
            get
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir = Path.Combine(baseDir, "BuhWise");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>
        /// Full path to SQLite database file.
        /// </summary>
        public static string DatabasePath =>
            Path.Combine(AppDataDirectory, "buhwise.db");

        /// <summary>
        /// Full path to error log file.
        /// </summary>
        public static string LogFilePath =>
            Path.Combine(AppDataDirectory, "error.log");
    }
}
