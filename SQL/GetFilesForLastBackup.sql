SELECT BMF.physical_device_name,
	   LB.backup_finish_date,
	   BMF.device_type
FROM  (
		SELECT TOP(1)	bs.media_set_id,
						bs.backup_finish_date
		FROM msdb.dbo.backupset bs
		WHERE bs.database_name = @db
		and bs.type = @backup_type
		ORDER BY backup_finish_date DESC
		) AS LB
JOIN msdb.dbo.backupmediafamily BMF ON LB.media_set_id = BMF.media_set_id