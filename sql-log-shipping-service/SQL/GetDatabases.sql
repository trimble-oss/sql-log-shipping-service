CREATE TABLE #LastRestore(
			destination_database_name SYSNAME NOT NULL PRIMARY KEY,
			backup_finish_date DATETIME NULL, 
			restore_date DATETIME NULL
);
WITH LR AS (
	SELECT	rh.destination_database_name,
			bs.backup_finish_date, 
			rh.restore_date,
			ROW_NUMBER() OVER(PARTITION BY rh.destination_database_name ORDER BY rh.restore_date  DESC) rnum
	FROM msdb.dbo.restorehistory rh
	JOIN msdb.dbo.backupset bs on rh.backup_set_id = bs.backup_set_id
	WHERE rh.restore_date > DATEADD(d,-14,GETUTCDATE())
	AND bs.type IN('D','I','L','P','Q')
	AND rh.destination_database_name IS NOT NULL
)
INSERT INTO #LastRestore WITH(TABLOCK) (destination_database_name,backup_finish_date,restore_date)
SELECT	LR.destination_database_name,
		LR.backup_finish_date,
		LR.restore_date
FROM LR
WHERE LR.rnum=1;

SELECT	d.name,
		DATEADD(mi,DATEDIFF(mi,GETDATE(),GETUTCDATE()),LR.backup_finish_date) AS backup_finish_date, 
		DATEADD(mi,DATEDIFF(mi,GETDATE(),GETUTCDATE()),LR.restore_date) AS restore_date
FROM sys.databases d 
LEFT OUTER JOIN #LastRestore LR ON d.name =  LR.destination_database_name
WHERE (d.state = 1 OR d.is_in_standby=1)
AND d.recovery_model <> 3 /* Exclude Simple */
/* Exclude databases with a restore in progress */
AND NOT EXISTS(	SELECT 1 
				FROM sys.dm_exec_requests R
				JOIN sys.dm_tran_locks L ON R.session_id = L.request_session_id
				WHERE R.command='RESTORE DATABASE'
				AND L.request_mode='X'
				AND L.resource_type='DATABASE'
				AND L.resource_database_id = d.database_id
				)
ORDER BY LR.backup_finish_date;