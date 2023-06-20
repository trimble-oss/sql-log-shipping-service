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

You need a Windows account with appropriate permissions to do the log restores.  A group managed service account is a good option as it avoids the need to manage passwords.  The installation command looks like this:

`LogShippingService.exe install -username "DOMAIN\GMSA$" -password ""`

The $ symbol is added for group managed service accounts and we also just use a blank password.

* Start the service

`net start LogShippingService`

* Review the log

The "Logs" folder contains a log of what the service is doing.

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
    ...
  }
```

## Include/Exclude Databases

Databases on the destination server are automatically included if they have **FULL** or **BULK LOGGED** recovery model and they are in a **RESTORING** or **STANDBY** state.  You can also explicitly include or exclude databases in the config.

Only log ship DB1, DB2, DB3 and DB4:

```json
  "Config": {
      "IncludedDatabases": ["DB1", "DB2", "DB3", "DB4"]
      ...
```

Log ship everything EXCEPT DB1, DB2, DB3 and DB4.

```json
  "Config": {
      "ExcludedDatabases": ["DB1", "DB2", "DB3", "DB4"]
      ...
```

## Uninstall

`LogShippingService.exe uninstall`

## How it works

The service will query for all databases in a restoring or standby state on the **destination** server, excluding any databases with SIMPLE recovery model where log backups don't apply.  The date of the last backup restored is also retrieved. It will loop over each database in parallel. The degree of parallelism is configurable with the **MaxThreads** parameter.  

For each database, it will query for any new log backup files in the Azure blob container. The **ContainerUrl** , **SASToken** and **LogFilePath** parameters are used here.  The **{DatabaseName}** token in the **LogFilePath** parameter will be replaced with the name of the database.  This provides flexibility to use different folder structures.  The date of the last backup restored is used when querying the blob container.  An **OffsetMins** parameter can be used if needed.

The **MaxProcessingTimeMins** parameter is used to prevent a single database that has fallen behind from impacting other databases.  If this is set to 60, the service will move on to the next database after 60 minutes of processing log restores for a single database.  Processing will continue for that database in the next iteration.  

Once the service has looped through all the databases, it will start the next iteration after a delay (**DelayBetweenIterationsMs**)

## Limitations

* The service doesn't handle the initial restore of the full backup from the primary.  
dbatools could be used for this purpose.  e.g.

`Copy-DbaDatabase -Source primarysql -Destination secondarysql -BackupRestore -UseLastBackup -NoRecovery -AllDatabases`

If the database already exists, it will be skipped.
* There is no configuration option to specify what databases to include/exclude. If the DB is in a restoring state with FULL or BULK LOGGED recovery it will be assumed to be part of the log shipping solution.  


