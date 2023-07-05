WITH LR AS (
	SELECT	rh.destination_database_name,
			bs.backup_finish_date, 
			rh.restore_date,
			ROW_NUMBER() OVER(PARTITION BY rh.destination_database_name ORDER BY rh.restore_date  DESC) rnum
	FROM msdb.dbo.restorehistory rh
	JOIN msdb.dbo.backupset bs on rh.backup_set_id = bs.backup_set_id
	WHERE rh.restore_date > DATEADD(d,-14,GETUTCDATE())

)
SELECT d.name,
		LR.backup_finish_date, 
		LR.restore_date
FROM sys.databases d 
LEFT OUTER JOIN LR ON d.name =  LR.destination_database_name AND LR.rnum=1
WHERE (d.state = 1 OR d.is_in_standby=1)
AND d.recovery_model <> 3 /* Exclude Simple */
/* Exclude databases with a restore in progress */
AND NOT EXISTS(	SELECT 1 
				FROM sys.dm_exec_requests R
				JOIN sys.dm_tran_locks L ON R.session_id = l.request_session_id
				WHERE R.command='RESTORE DATABASE'
				AND L.request_mode='X'
				AND L.resource_type='DATABASE'
				AND L.resource_database_id = d.database_id
				)
ORDER BY LR.backup_finish_date