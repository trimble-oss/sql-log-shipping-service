# Config File

The config file is appsettings.json.  There are example config files that can be used as a starting point.  

* [appsettings.json.azure.example](appsettings.json.azure.example) 
* [appsettings.json.unc.example](appsettings.json.unc.example)

:warning: **For UNC paths, the backslash character needs to be encoded.  `\` becomes `\\`.  A double backslash `\\` becomes `\\\\`.**

## Basic Config

### Destination

A connection string for the server where you want the log files to be restored.

```json
  "Config": {
    "Destination": "Data Source=LOCALHOST;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",
  }
```
⚠️*It's recommended to use Windows authentication as this is not encrypted.*

### LogFilePath

The path where your transaction log backups are located.  This should have a `{DatabaseName}` token which will be replaced by the name of the database.  The service will run the [GetDatabases](blob/main/SQL/GetDatabases.sql) query to get a list of databases that are in a restoring or standby state.  It will loop through each database looking for log backups that have occurred since the last restore.  The `{DatabaseName}` token is replaced with the name of the database been restored.

```json
  "Config": {
    "LogFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\LOG",
    //..
  }
```

Restores are also supported from Azure blob containers. In this case you will need to specify **ContainerUrl** and **SASToken**.

```json
  "Config": {
    "ContainerUrl": "https://your_storage_account.blob.core.windows.net/uour_container_name",
    "SASToken": "?sp=...",
    "LogFilePath": "LOG/SERVERNAME/{DatabaseName}/",
    //..
  }
````

🔐 *The SASToken value will be encrypted with the machine key when the service starts.*

## Initialization Options

The log shipping service can automatically initialize new databases created on the primary SQL instance.  This can either be done using the msdb backup history from the primary SQL instance or it can look for new database backups from disk or URL.  You can omit these options if you want to initialize your databases manually.

### MSDB

You can initialize new databases created on the primary instance by specifying a **SourceConnectionString**. 

```json
  "Config": {
      "SourceConnectionString": "Data Source=PRIMARY1;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",  
      //...
  }
```

To be initialized, the database should be online with FULL or BULK LOGGED recovery model.  The database needs a FULL backup and the backup location must be accessible on the target server.  If you use Ola Hallengren's backup solution, the @ChangeBackupType parameter can be used to create a FULL backup for new databases when the LOG backup job runs.  

If you are backing up to a local path, it's possible to do a string find/replace to convert it into a UNC path that is accessible on the secondary node: 

  ```json
  "Config": {
      "SourceConnectionString": "Data Source=PRIMARY1;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",  
      "MSDBPathFind": "B:\\Backup\\",
      "MSDBPathReplace": "\\\\SERVERNAME\\Backup\\"
  }
```     

### Disk 
To initialize from folder you also need to specify the folder locations for your FULL/DIFF backups.  Use the `{DatabaseName}` token in place of the database name.   

```json
  "Config": {
    "FullFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\FULL",
    "DiffFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\DIFF",
    //...
  }
```

In the example above, the service enumerates all the folders in the path `\\BACKUPSERVER\Backups\SERVERNAME\` (Taken from `\\BACKUPSERVER\Backups\SERVERNAME\{DatabaseName}\FULL`).  If processes each folder/database in parallel.  If the database doesn't exist if will restore the database from the last FULL/DIFF backup.

If you backup to multiple files, files with the same modified date are treated as part of the same backup set.  This avoids having to parse the file name and allows for different file naming formats.

### URL

To initialize from azure blob you need to specify **FullFilePath** and **DiffFilePath** using `{DatabaseName}` token in place of the database name.  If **ContainerUrl** and **SASToken** are specified these paths will be treated as azure blob paths rather than unc paths.  

```json
  "Config": {
    "ContainerUrl": "https://your_storage_account.blob.core.windows.net/your_container_name",
    "SASToken": "?sp=...",
    "FullFilePath": "FULL/SERVERNAME/{DatabaseName}/",
    "DiffFilePath": "DIFF/SERVERNAME/{DatabaseName}/",
    //...
  }
```

If you backup to multiple files, files with the same modified date are treated as part of the same backup set.  This avoids having to parse the file name and allows for different file naming formats.

⚠️ *Note: S3 isn't currently supported*

### Other initialization Options

#### Partial Backups and READONLY filegroups

Log Shipping can be initialized from PARTIAL backups.  Partial backups are backups created using the **READ_WRITE_FILEGROUPS** option.  This excludes any READONLY filegroups which you need to create separate backups for. Specify the location of your READONLY backups using **ReadOnlyFilePath**.  By default all filegroups will be required before a restore operation is attempted. If you want the RESTORE to proceed without all the READONLY filegroups, set **RecoverPartialBackupWithoutReadOnly** option to **true** (**ReadOnlyFilePath** can be omitted if **true**). 

```json
  "Config": {
    "LogFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\LOG",
    "FullFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\FULL",
    "DiffFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\DIFF",
    "ReadOnlyFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\READONLY",
    "RecoverPartialBackupWithoutReadOnly": false
    //...
  }
```

Note: If you are using PARTIAL backups, check sys.master_files for any files in the RECOVERY_PENDING state.  These files will need to be recovered.

#### Polling Frequency for new DBs

You can adjust the frequency it polls for new databases using **PollForNewDatabasesFrequency** (Specify a time in minutes.  Default 10min). Or you can use **PollForNewDatabasesCron** instead if you want to use a cron expression for more advanced scheduling.

#### Initialize Simple recovery DBs

It doesn't make sense to include SIMPLE recovery model DBs for log shipping so they are excluded by default.  It could be useful to include them in a disaster recovery scenario though by specifying **InitializeSimple**.  

#### Max Backup Age for Initialization

The log shipping service doesn't consider backups older than 14 days by default.  If you are initializing from disk this will prevent restoring a backup for an old database that was deleted.  It also prevents initializing from an old backup that will need a large amount of log files applied to bring it up-to-date.  If you need to restore from an older backup you can adjust **MaxBackupAgeForInitialization**.  

#### Moving Files
If you need to adjust the file paths for the initial restore the **MoveDataFolder**, **MoveLogFolder** and **MoveFileStreamFolder** can be specified.  If possible it's best to keep the drive configuration and file paths identical between the log shipping primary and secondary.

#### Example
```json
  "Config": {
      "PollForNewDatabasesFrequency" : 10,
      "InitializeSimple": true,
      "MaxBackupAgeForInitialization": 30,
      "MoveDataFolder": "D:\\Data",
      "MoveLogFolder": "L:\\Log",
      "MoveFileStreamFolder": "F:\\FileStream"
      //..
  }
```

## Schedule

The log shipping service works by looping through all the databases in parallel (Limited by **MaxThreads**) then waiting a period of time to start the next iteration.  You can control the delay between iterations with **DelayBetweenIterationsMs**.  If a single database has a large volume of log backups to restore it could delay the start of the next iteration.  The **MaxProcessingTimeMins** can be used to control the maximum amount of time to spend processing log restores for a single database.  

```json
  "Config": {
    "PollForNewDatabasesFrequency" : 10,
    "DelayBetweenIterationsMs": 10000,
    "MaxProcessingTimeMins":  60,
    //..
  }
```

If you want more control over when log restores run.  e.g. On the hour, every hour, use the **LogRestoreScheduleCron** instead of **DelayBetweenIterationsMs**.  Also use **PollForNewDatabasesCron** instead of **PollForNewDatabasesFrequency"** if you want to use a cron schedule for new database initialization.

```json
  "Config": {
    "LogRestoreScheduleCron": "0 * * * *",
    "PollForNewDatabasesCron": "0 * * * *",
    //..
  }
```
*This project uses [Cronos](https://github.com/HangfireIO/Cronos) to calculate the next run date from the cron expression.*

## Other

### Timezone Offset

The log shipping service will look for log backups created after the last restored backup.  It's possible that timezone differences could cause issues where we have to process additional log files or it starts processing log files that are too recent.  

```json
  "Config": {
    "OffsetMins": 0,
    //..
  }
```

*Note: If the log files are too recent, the log shipping service with automatically try adjusting the date by 1hr then by 1 day.*  

### Include/Exclude Databases

Databases on the destination server are automatically included if they have **FULL** or **BULK LOGGED** recovery model and they are in a **RESTORING** or **STANDBY** state.  You can also explicitly include or exclude databases in the config.

Only log ship DB1, DB2, DB3 and DB4:

```json
  "Config": {
      "IncludedDatabases": ["DB1", "DB2", "DB3", "DB4"]
      //...
  }
```

Log ship everything EXCEPT DB1, DB2, DB3 and DB4.

```json
  "Config": {
      "ExcludedDatabases": ["DB1", "DB2", "DB3", "DB4"]
      //...
  }
```

### Header verification

Header verification is done by default using RESTORE HEADERONLY to validate the backup before running RESTORE LOG.  This adds some extra overhead.  It's possible to turn off header verification using:

```json
  "Config": {
    "CheckHeaders": false,
    //..
  }
```

### Standby

If you want to be able to query the databases, add the **StandbyFileName** to the Config section of **appsettings.json**.  This should be a file path that contains the template **{DatabaseName}** to be replaced with the database name.

You can also adjust the **Hours** to control when database restores take place (Allowing time periods where the database is available for user queries).  The example below will allow the database to be available between 9:00 and 16:59 (Missing 9-16).  The **PollForNewDatabaseFrequency** is used to control how often new databases are initialized.

If users are able to query the log shipped databases, their connections can prevent future restore operations.  The default is to KILL user connections after 60 seconds.  This can be adjusted with **KillUserConnections** and **KillUserConnectionsWithRollbackAfter**.

```json
  "Config": {
    "StandbyFileName": "D:\\Standby\\{DatabaseName}_Standby.BAK",
    "Hours": [0, 1, 2, 3, 4, 5, 6, 7, 8, 17, 18, 19, 20, 21, 22, 23],
    "KillUserConnections": true,
    "KillUserConnectionsWithRollBackAfter": 60
    //...
  }
```

## Logging

[Serilog](https://serilog.net/) is used for logging.  In most cases you shouldn't need to modify this section of the config file.

```json
 "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.File" ],
    "MinimumLevel": "Debug",
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} <{ThreadId}>{NewLine}{Exception}"
        }
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/log-.txt",
          "rollingInterval": "Hour",
          "retainedFileCountLimit": 24,
          "outputTemplate": "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} <{ThreadId}>{NewLine}{Exception}"
        }
      }
    ],
    "Enrich": [ "FromLogContext", "WithMachineName", "WithThreadId" ],
    "Properties": {
      "Application": "LogShippingService"
    }
}
 ```
