SELECT name,
	    recovery_model,
		state,
		is_in_standby
FROM sys.databases
WHERE database_id>4