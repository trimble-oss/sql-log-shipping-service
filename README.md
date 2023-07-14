# SQL Log Shipping Service *(For Azure blob & UNC Paths)*

:warning: Project is in an early stage of development/testing.

This project provides a solution for automatically restoring SQL Server transaction log backups created using BACKUP TO URL to an Azure blob container.  The built-in log shipping solution is designed to work by backing up to a local path and doesn't support using BACKUP TO URL.  The solution also doesn't scale well if you have a large number of databases as it creates SQL Agent jobs per database.  If you have 1000 databases you don't want to have 1000 log backup or restore jobs executing simultaneously.  A single job might also be insufficient if you have a large number of databases.

[Ola'Hallegren's](https://ola.hallengren.com/) maintenance solution is a popular option for managing SQL Server backups.  It supports backup to URL and the **@DatabasesInParallel** option to allow you to use multiple jobs if a single job isn't sufficient.  Ola's backup solution has [good documentation](https://ola.hallengren.com/sql-server-backup.html) and [this gist](https://gist.github.com/scheffler/7edd40f430235aab651fadcc7d191a89) is also useful to help you get started with BACKUP TO URL.

This log shipping service provides a solution for restoring the log backups directly from the azure blob container.  This isn't possible with native log shipping or with [sp_AllNightLog](https://www.brentozar.com/sp_allnightlog/)

## Setup

* Extract the application binary files
* Create appsettings.json.

The log shipping service runs as a Windows service.  Configure the service using the "appsettings.json" configuration file.  There is an example config provided [appsettings.json.azure.example](appsettings.json.azure.example) or [appsettings.json.unc.example](appsettings.json.unc.example) that you can use as a starting point.  You should review and edit the entries in the "Config" section of the file.

**Azure**
```json
  "Config": {
    "ContainerUrl": "https://your_storage_account.blob.core.windows.net/uour_container_name",
    "SASToken": "?sp=...",
    "LogFilePath": "LOG/SERVERNAME/{DatabaseName}/",
    "MaxThreads": 10,
    "Destination": "Data Source=LOCALHOST;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",
    "DelayBetweenIterationsMs": 10000,
    "OffsetMins": 0,
    "MaxProcessingTimeMins":  60
  }
  ```

**UNC Path or directory**
```json
  "Config": {
    "LogFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\LOG",
    "MaxThreads": 10,
    "Destination": "Data Source=LOCALHOST;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",
    "DelayBetweenIterationsMs": 10000,
    "OffsetMins": 0,
    "MaxProcessingTimeMins":  60
  }
  ```
:warning: For UNC paths, the backslash character needs to be encoded.  "\\" becomes "\\\\".  A double backslash "\\\\" becomes "\\\\\\\\".

The SASToken value will be encrypted with the machine key when the service starts.  It's recommended to use Windows authentication for the "Destination" connection string.

* Install as a service

You need a Windows account with appropriate [permissions](#permissions) to do the log restores.  A group managed service account is a good option as it avoids the need to manage passwords.  The installation command looks like this:

`LogShippingService.exe install -username "DOMAIN\GMSA$" -password ""`

The $ symbol is added for group managed service accounts and we also just use a blank password.

* Start the service

`net start LogShippingService`

* Review the log

The "Logs" folder contains a log of what the service is doing.

## Permissions

The service account requires the following permissions:

* **dbcreator** Role membership
* **VIEW SERVER STATE**
* **CONNECT ANY DATABASE**.  Required if you are using STANDBY option with 'KillUserConnections'.

```powershell
# Grant permissions using dbatools.  
# Replace DOMAIN\YOURUSER, adding a $ to the end of the username if using a managed service account.
# Run locally or replace LOCALHOST with the name of the server
Add-DbaServerRoleMember -SqlInstance  LOCALHOST -ServerRole dbcreator -Login DOMAIN\YOURUSER
Invoke-DbaQuery -SqlInstance LOCALHOST -Query "GRANT VIEW SERVER STATE TO [DOMAIN\YOURUSER];GRANT CONNECT ANY DATABASE TO [DOMAIN\YOURUSER]"
```

Additionally the service also requires: 

* File system permissions to write to application folder (To write to Logs folder.)
* File system permissions to list backup files (if using a unc path instead of azure blob)

*The service account running SQL also requires access to read the backup files*

## Standby

If you want to be able to query the databases, add the **StandbyFileName** to the Config section of **appsettings.json**.  This should be a file path that contains the template **{DatabaseName}** to be replaced with the database name.

You can also adjust the **Hours** to control when database restores take place (Allowing time periods where the database is available for user queries).  The example below will allow the database to be available between 9:00 and 16:59 (Missing 9-16).  The **DelayBetweenIterationsMs** could also be adjusted.

If users are able to query the log shipped databases, their connections can prevent future restore operations.  The default is to KILL user connections after 60 seconds.  This can be adjusted with **KillUserConnections** and **KillUserConnectionsWithRollbackAfter**.

```json
  "Config": {
    "StandbyFileName": "D:\\Standby\\{DatabaseName}_Standby.BAK",
    "Hours": [0, 1, 2, 3, 4, 5, 6, 7, 8, 17, 18, 19, 20, 21, 22, 23],
    "DelayBetweenIterationsMs": 10000,
    "KillUserConnections": true,
    "KillUserConnectionsWithRollBackAfter": 60
    //...
  }
```

## Include/Exclude Databases

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

## Header verification

Header verification is done by default using RESTORE HEADERONLY to validate the backup before running RESTORE LOG.  This adds some extra overhead.  It's possible to turn off header verification using:

```json
  "Config": {
    "CheckHeaders": false,
    //..
  }
```

## Initialization for new databases

### Using msdb history from primary

You can initialize new databases created on the primary instance by specifying a **SourceConnectionString**. 

```json
  "Config": {
      "SourceConnectionString": "Data Source=PRIMARY1;Integrated Security=True;Encrypt=True;Trust Server Certificate=True",  
      //...
  }
```

To be initialized, the database should be online with FULL or BULK LOGGED recovery model.  The database needs a FULL backup and the backup location must be accessible on the target server.  If you use Ola Hallengren's backup solution, the @ChangeBackupType parameter can be used to create a FULL backup for new databases when the LOG backup job runs.

### From backup folder

To initialize from folder you also need to specify the folder locations for your FULL/DIFF backups.  Use the *{DatabaseName}* token in place of the database name.  <u>Don't</u> include a **SourceConnectionString** otherwise initialization will be done from msdb history instead.  

```json
  "Config": {
    "LogFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\LOG",
    "FullFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\FULL",
    "DiffFilePath": "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\DIFF",
    //...
  }
```

In the example above, the service enumerates all the folders in the path "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\" (Taken from "\\\\BACKUPSERVER\\Backups\\SERVERNAME\\{DatabaseName}\\FULL").  If processes each folder/database in parallel.  If the database doesn't exist if will restore the database from the last FULL/DIFF backup.

If you backup to multiple files, files with the same modified date are treated as part of the same backup set.  This avoids having to parse the file name and allows for different file naming formats.

## From Azure blob

To initialize from azure blob you need to specify **FullFilePath** and **DiffFilePath** using *{DatabaseName}* token in place of the database name.  If **ContainerUrl** and **SASToken** are specified these paths will be treated as azure blob paths rather than unc paths.  

```json
  "Config": {
    "ContainerUrl": "https://your_storage_account.blob.core.windows.net/your_container_name",
    "SASToken": "?sp=...",
    "LogFilePath": "LOG/SERVERNAME/{DatabaseName}/",
    "FullFilePath": "FULL/SERVERNAME/{DatabaseName}/",
    "DiffFilePath": "DIFF/SERVERNAME/{DatabaseName}/",
    //...
  }
```

If you backup to multiple files, files with the same modified date are treated as part of the same backup set.  This avoids having to parse the file name and allows for different file naming formats.

### Partial Backups and READONLY filegroups

Log Shipping can be initialized from PARTIAL backups.  Partial backups are backups created using the **READ_WRITE_FILEGROUPS** option.  This excludes any READONLY filegroups which you need to create separate backups for. Specify the location of your READONLY backups using **ReadOnlyFilePath**.   By default all filegroups will be required before a restore operation is attempted. If you want the RESTORE to proceed without all the READONLY filegroups, set **RecoverPartialBackupWithoutReadOnly** option to **true** (**ReadOnlyFilePath** can be omitted if **true**). 

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

### Other options for initialization

You can adjust the frequency it polls for new databases using **PollForNewDatabasesFrequency** (Specify a time in minutes.  Default 1min).  

Databases that already exist on the target server are skipped. Other databases can be excluded by specifying **ExcludedDatabases**. See [Include/Exclude Databases](#includeexclude-databases)

It doesn't make sense to include SIMPLE recovery model DBs for log shipping so they are excluded by default.  It could be useful to include them in a disaster recovery scenario though by specifying **InitializeSimple**.  

The log shipping service doesn't consider backups older than 14 days by default.  If you are initializing from disk this will prevent restoring a backup for an old database that was deleted.  It also prevents initializing from an old backup that will need a large amount of log files applied to bring it up-to-date.  If you need to restore from an older backup you can adjust **MaxBackupAgeForInitialization**.  

If you need to adjust the file paths for the initial restore the **MoveDataFolder**, **MoveLogFolder** and **MoveFileStreamFolder** can be specified.  If possible it's best to keep the drive configuration and file paths identical between the log shipping primary and secondary.

```json
  "Config": {
      "PollForNewDatabasesFrequency" : 10,
      "ExcludedDatabases": ["LSExcluded1", "LSExcluded2"],
      "InitializeSimple": true,
      "MaxBackupAgeForInitialization": 30,
      "MoveDataFolder": "D:\\Data",
      "MoveLogFolder": "L:\\Log",
      "MoveFileStreamFolder": "F:\\FileStream"
      //..
  }
```

## Uninstall

`LogShippingService.exe uninstall`

## How it works

The service will query for all databases in a restoring or standby state on the **destination** server, excluding any databases with SIMPLE recovery model where log backups don't apply.  The date of the last backup restored is also retrieved. It will loop over each database in parallel. The degree of parallelism is configurable with the **MaxThreads** parameter.  

For each database, it will query for any new log backup files in the Azure blob container. The **ContainerUrl** , **SASToken** and **LogFilePath** parameters are used here.  The **{DatabaseName}** token in the **LogFilePath** parameter will be replaced with the name of the database.  This provides flexibility to use different folder structures.  The date of the last backup restored is used when querying the blob container.  An **OffsetMins** parameter can be used if needed.

The **MaxProcessingTimeMins** parameter is used to prevent a single database that has fallen behind from impacting other databases.  If this is set to 60, the service will move on to the next database after 60 minutes of processing log restores for a single database.  Processing will continue for that database in the next iteration.  

Once the service has looped through all the databases, it will start the next iteration after a delay (**DelayBetweenIterationsMs**)


