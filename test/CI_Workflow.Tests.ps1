Describe 'CI Workflow checks' {

    It 'Test Standby Count' {
        # Expect 3 DBs to be in standby state
         @(Get-DbaDatabase -SqlInstance "LOCALHOST" -Status "Standby").Count | Should -Be 3
    }
    It '2 logical backups in same physical file check' {
        # LogShipping1_2.trn as 2 logical backups.  If LogShipping1_3.trn is restored, then we handled it OK
         $results= Invoke-DbaQuery -SqlInstance "LOCALHOST" -As PSObject -Query "SELECT TOP(1) bmf.physical_device_name AS LastLogFile
         FROM msdb.dbo.restorehistory rsh
         INNER JOIN msdb.dbo.backupset bs ON rsh.backup_set_id = bs.backup_set_id
         INNER JOIN msdb.dbo.restorefile rf ON rsh.restore_history_id = rf.restore_history_id
         INNER JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
         WHERE rsh.restore_type = 'L'
         AND rsh.destination_database_name='LogShipping1'
         ORDER BY rsh.restore_history_id DESC;"

         $results.LastLogFile | Should -Be "C:\Backup\LOG\LogShipping1\LogShipping1_3.trn"        
    }

}