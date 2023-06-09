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
ORDER BY LR.backup_finish_date