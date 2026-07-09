Describe 'CI Workflow checks - No Re-initialize (safety)' {

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

	It 'Copies are still in standby' {
		@(Get-DbaDatabase -SqlInstance "LOCALHOST" -Status "Standby" -Database "ReInit1_Copy","ReInit2_Copy").Count | Should -Be 2
	}

	Context 'ReInit1 - source re-created but re-initialization disabled' {
		It 'Copy still reflects the original incarnation (marker = 1)' {
			Get-Marker 'ReInit1_Copy' | Should -Be 1
		}
		It 'Copy was NOT re-initialized (restored from FULL only once)' {
			Get-FullRestoreCount 'ReInit1_Copy' | Should -Be 1
		}
	}

	Context 'ReInit2 - broken log chain but re-initialization disabled' {
		It 'Copy still reflects the original chain (marker = 1)' {
			Get-Marker 'ReInit2_Copy' | Should -Be 1
		}
		It 'Copy was NOT re-initialized (restored from FULL only once)' {
			Get-FullRestoreCount 'ReInit2_Copy' | Should -Be 1
		}
	}

	It 'The audit log warns that re-initialization is required but disabled' {
		# With EnableReinitialization disabled the service still detects the broken chains and logs that
		# re-initialization is required (mentioning EnableReinitialization) - this is expected and desirable.
		($script:ReinitLog -match 'Set EnableReinitialization to true').Count | Should -BeGreaterThan 0
	}

	It 'No database was actually dropped or re-initialized' {
		# The destructive actions must NOT have run.  Only match audit phrases that are written when a drop /
		# re-initialization actually executes - NOT the "must be dropped and re-initialized" advisory that the
		# disabled path logs (which merely tells the operator what would be required).
		($script:ReinitLog -match 'Dropping .* so it can be re-initialized|has been dropped and re-initialized from a current backup').Count | Should -Be 0
	}
}
