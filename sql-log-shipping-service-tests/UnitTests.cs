using LogShippingService;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace LogShippingServiceTests
{
    [TestClass]
    public class UnitTests
    {
        [TestMethod]
        public void TestMethod1()
        {
            // Start with a blank config file
            File.Delete(Config.ConfigFile);
            File.WriteAllText(Config.ConfigFile, "{}");
            var options = new Config()
            {
                ContainerUrl = "https://myaccount.blob.core.windows.net/mycontainer",
                Destination = "Server=.;Database=master;Integrated Security=true;",
                DiffFilePath = @"\\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\DIFF",
                FullFilePath = @"\\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL",
                LogFilePath = @"\\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL",
                MaxBackupAgeForInitialization = 7,
                MoveDataFolder = "D:\\Data",
                MoveFileStreamFolder = "F:\\FileStream",
                MoveLogFolder = "L:\\Log",
                MSDBPathFind = "C:\\Backup",
                MSDBPathReplace = "\\\\BackupServer\\Backup",
                PollForNewDatabasesCron = "*/5 * * * *",
                PollForNewDatabasesFrequency = 5,
                ReadOnlyFilePath = @"\\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\READONLY",
                RecoverPartialBackupWithoutReadOnly = true,
                SASToken = "mySASToken",
                SourceConnectionString = "Server=.;Database=master;Integrated Security=true;",
                Hours = new HashSet<int> { 1, 2, 3, 4, 5 },
                StandbyFileName = "D:\\Data\\{DatabaseName}_standby.bak",
                KillUserConnectionsWithRollbackAfter = 5,
                KillUserConnections = false
            };
            // Pass values to LogShippingService.exe command line
            var commandLine = $"--ContainerUrl {options.ContainerUrl} --Destination \"{options.Destination}\" --DiffFilePath \"{options.DiffFilePath}\" --FullFilePath \"{options.FullFilePath}\" --LogFilePath \"{options.LogFilePath}\" --MaxBackupAgeForInitialization {options.MaxBackupAgeForInitialization} --MoveDataFolder \"{options.MoveDataFolder}\" --MoveFileStreamFolder \"{options.MoveFileStreamFolder}\" --MoveLogFolder \"{options.MoveLogFolder}\" --MSDBPathFind \"{options.MSDBPathFind}\" --MSDBPathReplace \"{options.MSDBPathReplace}\" --PollForNewDatabasesCron \"{options.PollForNewDatabasesCron}\" --PollForNewDatabasesFrequency {options.PollForNewDatabasesFrequency} --ReadOnlyFilePath \"{options.ReadOnlyFilePath}\" --RecoverPartialBackupWithoutReadOnly {options.RecoverPartialBackupWithoutReadOnly} --SASToken \"{options.SASToken}\" --SourceConnectionString \"{options.SourceConnectionString}\" --Hours {string.Join(' ', options.Hours)} --StandbyFileName \"{options.StandbyFileName}\" --KillUserConnectionsWithRollbackAfter {options.KillUserConnectionsWithRollbackAfter} --KillUserConnections {options.KillUserConnections}";

            // Call LogShippingService.exe with the command line arguments
            var p = new Process()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "LogShippingService.exe",
                    Arguments = commandLine,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            p.Start();

            // Wait for the process to exit
            p.WaitForExit();
            Assert.AreEqual(0, p.ExitCode);

            // Read the process output
            string output = p.StandardOutput.ReadToEnd();
            Assert.AreNotSame(output, "");

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();

            var config = configuration.GetSection("Config").Get<Config>() ?? new Config();

            Assert.AreEqual(options.ContainerUrl, config.ContainerUrl);
            Assert.AreEqual(options.Destination, config.Destination);
            Assert.AreEqual(options.DiffFilePath, config.DiffFilePath);
            Assert.AreEqual(options.FullFilePath, config.FullFilePath);
            Assert.AreEqual(options.LogFilePath, config.LogFilePath);
            Assert.AreEqual(options.MaxBackupAgeForInitialization, config.MaxBackupAgeForInitialization);
            Assert.AreEqual(options.MoveDataFolder, config.MoveDataFolder);
            Assert.AreEqual(options.MoveFileStreamFolder, config.MoveFileStreamFolder);
            Assert.AreEqual(options.MoveLogFolder, config.MoveLogFolder);
            Assert.AreEqual(options.MSDBPathFind, config.MSDBPathFind);
            Assert.AreEqual(options.MSDBPathReplace, config.MSDBPathReplace);
            Assert.AreEqual(options.PollForNewDatabasesCron, config.PollForNewDatabasesCron);
            Assert.AreEqual(options.PollForNewDatabasesFrequency, config.PollForNewDatabasesFrequency);
            Assert.AreEqual(options.ReadOnlyFilePath, config.ReadOnlyFilePath);
            Assert.AreEqual(options.RecoverPartialBackupWithoutReadOnly, config.RecoverPartialBackupWithoutReadOnly);
            Assert.AreEqual(options.SASToken, config.SASToken);
            Assert.AreEqual(options.SourceConnectionString, config.SourceConnectionString);
            Assert.AreEqual(options.StandbyFileName, config.StandbyFileName);
            Assert.AreEqual(options.KillUserConnectionsWithRollbackAfter, config.KillUserConnectionsWithRollbackAfter);
            Assert.AreEqual(options.KillUserConnections, config.KillUserConnections);
            CollectionAssert.AreEquivalent(options.Hours.ToList(), config.Hours.ToList());
        }
    }
}