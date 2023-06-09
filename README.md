# SQL Log Shipping Service (For Azure blob)

:warning: Project is in an early stage of development/testing.

This project provides a solution for automatically restoring SQL Server transaction log backups created using BACKUP TO URL to an Azure blob container.  The built-in log shipping solution is designed to work by backing up to a local path and doesn't support using BACKUP TO URL.  The solution also doesn't scale well if you have a large number of databases as it creates SQL agent jobs per database.  If you have 1000 databases you don't want to have 1000 log backup or restore jobs executing simultaneously.  A single job might also be insufficient if you have a large number of databases.

[Ola'Hallegren's](https://ola.hallengren.com/) maintenance solution is a popular option for managing SQL Server backups.  It supports backup to URL and the **@DatabasesInParallel** option to allow you to use multiple jobs if a single job isn't sufficient.  Ola's backup solution has [good documentation](https://ola.hallengren.com/sql-server-backup.html) and [this gist](https://gist.github.com/scheffler/7edd40f430235aab651fadcc7d191a89) is also useful to help you get started with BACKUP TO URL.

This log shipping service provides a solution for restoring the log backups directly from the azure blob container.  This isn't possible with native log shipping or with [sp_AllNightLog](https://www.brentozar.com/sp_allnightlog/)

## Setup

* Extract the application binary files
* Create appsettings.json.

The log shipping service runs as a Windows service.  Configure the service using the "appsettings.json" configuration file.  There is an example config provided "appsettings.json.example" that you can use as a starting point.  You should review and edit the entries in the "Config" section of the file.

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

The SASToken value will be encrypted with the machine key when the service starts.  It's recommended to use Windows authentication for the "Destination" connection string.

* Install as a service

You need a Windows account with appropriate permissions to do the log restores.  A group managed service account is a good option as it avoids the need to manage passwords.  The installation command looks like this:

`LogShippingService.exe install -username "DOMAIN\GMSA$" -password ""`

The $ symbol is added for group managed service accounts and we also just use a blank password.

* Start the service

`net start LogShippingService`

* Review the log

The "Logs" folder contains a log of what the service is doing.

## Uninstall

`LogShippingService.exe uninstall`

## How it works

The service will query for all databases in a restoring or standby state on the **destination** server, excluding any databases with SIMPLE recovery model where log backups don't apply.  The date of the last backup restored is also retrieved. It will loop over each database in parallel. The degree of parallelism is configurable with the **MaxThreads** parameter.  

For each database, it will query for any new log backup files in the Azure blob container. The **ContainerUrl** , **SASToken** and **LogFilePath** parameters are used here.  The **{DatabaseName}** token in the **LogFilePath** parameter will be replaced with the name of the database.  This provides flexibility to use different folder structures.  The date of the last backup restored is used when querying the blob container.  An **OffsetMins** parameter can be used if needed.

The **MaxProcessingTimeMins** parameter is used to prevent a single database that has fallen behind from impacting other databases.  If this is set to 60, the service will move on to the next database after 60 minutes of processing log restores for a single database.  Processing will continue for that database in the next iteration.  

Once the service has looped through all the databases, it will start the next iteration after a delay (**DelayBetweenIterationsMs**)

## Limitations

* This service currently only works with backups in Azure blob
* The service doesn't handle the initial restore of the full backup from the primary.  
dbatools could be used for this purpose.  e.g.

`Copy-DbaDatabase -Source primarysql -Destination secondarysql -BackupRestore -UseLastBackup -NoRecovery -AllDatabases`

If the database already exists, it will be skipped.
* There is no configuration option to specify what databases to include/exclude. If the DB is in a restoring state with FULL or BULK LOGGED recovery it will be assumed to be part of the log shipping solution.  


