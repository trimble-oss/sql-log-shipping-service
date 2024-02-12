# SQL Log Shipping Service 

This project provides a solution for automatically restoring SQL Server transaction log backups. Implemented as a .NET application that runs as a Windows service, it provides the following features:

* **Simple config based setup.** *Eliminates the need for per-database basis configuration.  Very effective for handling a large number of databases*
* **Automatic initialization of new databases**. *Incorporate new databases without any manual intervention*
* **UNC path or URL (Azure Blob).** *Additional flexibility to work directly with Azure blob containers or with standard UNC paths.  e.g. `\\server\share`*
* **Scalable.** *The log shipping service allows for a configurable number of threads so your SQL instance isn't overloaded with a job per database or constrained by a single job.  The service is efficient and can scale to a large number of databases*
* **Standby mode support.** *Allow users to query your log shipped databases.  Prevent open connections from blocking log restores with options to kill sessions after a period of time. Keep your databases available for querying during certain hours.  Standby option is only applied after the last log is restored (more efficient than built-in log shipping)*
* **A disaster recovery tool**.  *Beyond the tools primary capability as a log shipping tool, it can also be used as part of your disaster recovery strategy to restore your databases from backup.*

The log shipping service is designed to handle restores only.  For backups, consider using [Ola'Hallegren's](https://ola.hallengren.com/) maintenance solution.  You can backup in parallel with the **@DatabasesInParallel** if you have a large number of databases.  You can also backup [direct to URL](https://gist.github.com/scheffler/7edd40f430235aab651fadcc7d191a89).  Ola's maintenance solution has good [documentation](https://ola.hallengren.com/sql-server-backup.html) to get you started.

## Prerequisites

You need a Windows account with appropriate [permissions](#permissions) to do log restores.  A group managed service account is a good option as it avoids the need to manage passwords.  

## Setup

* Extract the application binary files.  e.g. `C:\sql-log-shipping-service`
* Create appsettings.json. [🔗See here](docs/config.md) for documentation on creating the config file.

The log shipping service runs as a Windows service and is configured using a [appsettings.json](docs/config.md) file.  There is an example config provided [appsettings.json.azure.example](appsettings.json.azure.example) or [appsettings.json.unc.example](appsettings.json.unc.example) that you can use as a starting point.  You should review and edit the entries in the [Config](/docs/config.md) section of the file. The [SeriLog](docs/config.md#logging) section can be ignored.

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
  },
  "Serilog": {
  //...
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
  },
  "Serilog": {
  //...
  ```
:warning: **For UNC paths, the backslash character needs to be encoded.  `\` becomes `\\`.  A double backslash `\\` becomes `\\\\`.**

* Install as a service

To install as a service, use [sc create](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create) (Change the **binpath** as required.):

**Group Managed Service Account example:**

`sc.exe create "LogShippingService" binpath="C:\sql-log-shipping-service\LogShippingService.exe" start=delayed-auto obj=DOMAIN\LogShippingGMSA$`

**Regular Domain User example:**

`sc.exe create "LogShippingService" binpath="C:\sql-log-shipping-service\LogShippingService.exe" start=delayed-auto obj=DOMAIN\LogShippingUser password=YourPasswordHere`

*You can omit obj and password to run as `LocalSystem`, but it's recommended to use a domain account for Windows authentication.*

* Start the service

`net start LogShippingService`

* Review the log

The *Logs* folder contains a log of what the service is doing.

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

* File system permissions to write to application folder *(For writing to Logs folder.)*

⚠️*Restrict access to the application folder for other user accounts*

* File system permissions to list backup files (if using a unc path instead of azure blob)

*The service account running SQL also requires access to read the backup files*

## Uninstall

To uninstall, stop the service then remove it with [sc delete](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-delete)

`net stop LogShippingService`

`sc.exe delete LogShippingService`

*If you delete the service before stopping it, a reboot will be required before the service is removed*

## How it works

The service will [query](blob/main/SQL/GetDatabases.sql) for all databases in a restoring or standby state on the **destination** server, excluding any databases with SIMPLE recovery model where log backups don't apply.  The date of the last backup restored is also retrieved. It will loop over each database in parallel. The degree of parallelism is configurable with the **MaxThreads** parameter.  

For each database, it will query for any new log backup files in the UNC path or Azure blob container. The **{DatabaseName}** token in the **LogFilePath** parameter will be replaced with the name of the database.  This provides flexibility to use different folder structures.  The date of the last backup restored is used to get a list of new log files to be restored.  An **OffsetMins** parameter can be used to handle timezone differences if needed.

The **MaxProcessingTimeMins** parameter is used to limit the amount of time that will be spent processing an individual database.  If this is set to 60, the service will move on to the next database after 60 minutes of processing log restores for a single database.  This limits the impact that a single database can have on log shipping for the whole server.  If a database reaches the max processing time it will pick up where it left off on the next iteration.

Once the service has looped through all the databases, it will start the next iteration after a delay (**DelayBetweenIterationsMs**)

## Monitoring & Troubleshooting

It's important to monitor your log shipping.  The standard transaction log shipping status report isn't available with custom log shipping implementations.  [DBA Dash](https://dbadash.com/) has log shipping monitoring that will work with custom log shipping implementations like this.  You can query the msdb history tables instead to check the health of your log shipping.  e.g.

```sql
WITH t AS (
    SELECT  rsh.destination_database_name,
			      bs.backup_finish_date,
            rsh.restore_date,
            bmf.physical_device_name,
            ROW_NUMBER() OVER (PARTITION BY rsh.destination_database_name ORDER BY rsh.restore_date DESC) rnum,
            bs.is_force_offline
    FROM msdb.dbo.restorehistory rsh
    INNER JOIN msdb.dbo.backupset bs ON rsh.backup_set_id = bs.backup_set_id
    INNER JOIN msdb.dbo.restorefile rf ON rsh.restore_history_id = rf.restore_history_id
    INNER JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
    WHERE rsh.restore_type = 'L'
)
SELECT	t.destination_database_name,
		t.backup_finish_date,
		t.restore_date,
		DATEDIFF(mi,t.restore_date,GETDATE()) as MinsSinceLastRestore,
		DATEDIFF(mi,t.backup_finish_date,GETDATE()) AS TotalTimeBehindMins,
		t.physical_device_name,
		t.is_force_offline AS IsTailLog,
		CASE WHEN t.is_force_offline=1 THEN 'RESTORE DATABASE ' + QUOTENAME(t.destination_database_name) + ' WITH RECOVERY' ELSE NULL END AS [Restore Command (Printed if tail log is restored)]
FROM t
WHERE t.rnum=1;
```

If you have issues with log shipping, check the **Logs** folder.  If you don't have any logs it's possible that the service account doesn't have permissions to write to the application folder.  Check the permissions on the folder.  You can also try running the application directly where it will output it's logs to the console.  

Some common issue to check for new configurations:

* [Check permissions](#permissions)
* Check the [config](docs/config.md) file is valid.  Ensure all `\` in paths are replaced with `\\`.  It's also easy to miss commas etc.