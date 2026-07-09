using System.Numerics;
using LogShippingService;

namespace LogShippingServiceTests
{
    /// <summary>
    /// Table-driven unit tests for <see cref="LogShipping.GetLogChainStatus"/>.
    /// This is the pure decision logic that classifies whether a log backup continues the current chain,
    /// belongs to a re-created source database, or begins a broken/reset chain.  These tests pin down every
    /// branch (including the BackupFinishDate stale-file guard) without requiring a SQL Server instance.
    /// </summary>
    [TestClass]
    public class LogChainStatusTests
    {
        private static readonly DateTime Reference = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static BackupHeader Header(
            DateTime? backupFinishDate = null,
            DateTime? databaseCreationDate = null,
            bool beginsLogChain = false,
            long firstLSN = 100,
            long lastLSN = 200)
        {
            return new BackupHeader
            {
                BackupFinishDate = backupFinishDate ?? Reference.AddMinutes(10),
                DatabaseCreationDate = databaseCreationDate ?? Reference.AddDays(-30),
                BeginsLogChain = beginsLogChain,
                FirstLSN = new BigInteger(firstLSN),
                LastLSN = new BigInteger(lastLSN)
            };
        }

        [TestMethod]
        public void StaleFile_NotNewerThanReference_IsOk_EvenWhenBeginsNewChain()
        {
            // A backup whose finish date is not newer than the last log we restored must never be treated as
            // a broken chain - guards the destructive re-initialization against stale / out-of-order files.
            var header = Header(
                backupFinishDate: Reference,               // == reference (boundary: <=)
                databaseCreationDate: Reference.AddDays(1), // newer incarnation ...
                beginsLogChain: true,                       // ... and begins a new chain ...
                firstLSN: 500);                             // ... ahead of the redo point

            var status = LogShipping.GetLogChainStatus(header, Reference.AddDays(-30), Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Ok, status);
        }

        [TestMethod]
        public void StaleFile_FinishDateOlderThanReference_IsOk()
        {
            var header = Header(backupFinishDate: Reference.AddMinutes(-5), databaseCreationDate: Reference.AddDays(1), beginsLogChain: true, firstLSN: 500);

            var status = LogShipping.GetLogChainStatus(header, Reference.AddDays(-30), Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Ok, status);
        }

        [TestMethod]
        public void NewerDatabaseCreationDate_IsRecreated()
        {
            var header = Header(
                backupFinishDate: Reference.AddMinutes(10),
                databaseCreationDate: Reference.AddDays(1)); // newer than the current chain's creation date

            var status = LogShipping.GetLogChainStatus(header, Reference.AddDays(-30), Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Recreated, status);
        }

        [TestMethod]
        public void RecreatedTakesPrecedenceOverBrokenChain()
        {
            // Both a newer creation date and BeginsLogChain - re-created is the more specific classification.
            var header = Header(
                backupFinishDate: Reference.AddMinutes(10),
                databaseCreationDate: Reference.AddDays(1),
                beginsLogChain: true,
                firstLSN: 500);

            var status = LogShipping.GetLogChainStatus(header, Reference.AddDays(-30), Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Recreated, status);
        }

        [TestMethod]
        public void BeginsLogChain_AheadOfRedoPoint_IsBroken()
        {
            // Same incarnation (creation date matches) but a new chain has started ahead of our redo point
            // e.g. the recovery model was switched to SIMPLE and back to FULL.
            var creationDate = Reference.AddDays(-30);
            var header = Header(
                backupFinishDate: Reference.AddMinutes(10),
                databaseCreationDate: creationDate,
                beginsLogChain: true,
                firstLSN: 500);

            var status = LogShipping.GetLogChainStatus(header, creationDate, Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Broken, status);
        }

        [TestMethod]
        public void BeginsLogChain_NoReferenceChain_IsBroken()
        {
            // No current chain reference yet - a log that begins a new chain can't be bridged.
            var header = Header(backupFinishDate: Reference.AddMinutes(10), beginsLogChain: true, firstLSN: 50);

            var status = LogShipping.GetLogChainStatus(header, currentChainCreationDate: null, Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Broken, status);
        }

        [TestMethod]
        public void BeginsLogChain_NullRedoStart_IsBroken()
        {
            var creationDate = Reference.AddDays(-30);
            var header = Header(backupFinishDate: Reference.AddMinutes(10), databaseCreationDate: creationDate, beginsLogChain: true, firstLSN: 50);

            var status = LogShipping.GetLogChainStatus(header, creationDate, Reference, redoStart: null);

            Assert.AreEqual(LogShipping.LogChainStatus.Broken, status);
        }

        [TestMethod]
        public void BeginsLogChain_AtOrBeforeRedoPoint_WithReferenceChain_IsOk()
        {
            // Our own chain's original first log re-appearing (pulled in by a reProcess walk-back) is safe to skip.
            var creationDate = Reference.AddDays(-30);
            var header = Header(
                backupFinishDate: Reference.AddMinutes(10),
                databaseCreationDate: creationDate,
                beginsLogChain: true,
                firstLSN: 100); // FirstLSN == redoStart (not ahead)

            var status = LogShipping.GetLogChainStatus(header, creationDate, Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Ok, status);
        }

        [TestMethod]
        public void ContinuesChain_NotBeginningNewChain_IsOk()
        {
            var creationDate = Reference.AddDays(-30);
            var header = Header(
                backupFinishDate: Reference.AddMinutes(10),
                databaseCreationDate: creationDate,
                beginsLogChain: false,
                firstLSN: 500);

            var status = LogShipping.GetLogChainStatus(header, creationDate, Reference, redoStart: 100);

            Assert.AreEqual(LogShipping.LogChainStatus.Ok, status);
        }
    }
}
