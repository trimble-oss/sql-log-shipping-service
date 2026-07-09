Describe 'CI Workflow checks - Re-initialize' {

    BeforeAll {
        function Get-FullRestoreCount($db) {
            (Invoke-DbaQuery -SqlInstance "LOCALHOST" -As PSObject -Query "SELECT COUNT(*) AS FullRestores
                FROM msdb.dbo.restorehistory
                WHERE destination_database_name = '$db'
                AND restore_type = 'D';").FullRestores
        }
        function Get-Marker($db) {
            (Invoke-DbaQuery -SqlInstance "LOCALHOST" -As PSObject -Database $db -Query "SELECT MAX(Id) AS Id FROM dbo.Marker").Id
        }
        $script:ReinitLog = @(@(Get-ChildItem -Path "C:\sql-log-shipping-service\Logs\reinitialization-*.txt" -ErrorAction SilentlyContinue) | Get-Content)
    }

    It 'All copies are in standby' {
        @(Get-DbaDatabase -SqlInstance "LOCALHOST" -Status "Standby" -Database "ReInit1_Copy","ReInit2_Copy","ReInit3_Copy").Count | Should -Be 3
    }

    Context 'ReInit1 - source database re-created' {
        It 'Copy reflects the new incarnation (marker = 2)' {
            Get-Marker 'ReInit1_Copy' | Should -Be 2
        }
        It 'Copy was re-initialized exactly once (restored from FULL exactly twice)' {
            Get-FullRestoreCount 'ReInit1_Copy' | Should -Be 2
        }
        It 'Re-initialization was recorded in the audit log' {
            ($script:ReinitLog -match 'ReInit1_Copy').Count | Should -BeGreaterThan 0
        }
    }

    Context 'ReInit2 - recovery model switched to SIMPLE and back to FULL' {
        It 'Copy reflects the data after the broken chain (marker = 2)' {
            Get-Marker 'ReInit2_Copy' | Should -Be 2
        }
        It 'Copy was re-initialized exactly once (restored from FULL exactly twice)' {
            Get-FullRestoreCount 'ReInit2_Copy' | Should -Be 2
        }
        It 'Re-initialization was recorded in the audit log' {
            ($script:ReinitLog -match 'ReInit2_Copy').Count | Should -BeGreaterThan 0
        }
    }

    Context 'ReInit3 - normal continuation (must NOT be re-initialized)' {
        It 'Copy received the new data via normal log restore (marker = 2)' {
            Get-Marker 'ReInit3_Copy' | Should -Be 2
        }
        It 'Copy was NOT re-initialized (restored from FULL only once)' {
            Get-FullRestoreCount 'ReInit3_Copy' | Should -Be 1
        }
        It 'Copy does not appear in the re-initialization audit log' {
            ($script:ReinitLog -match 'ReInit3_Copy').Count | Should -Be 0
        }
    }

}
