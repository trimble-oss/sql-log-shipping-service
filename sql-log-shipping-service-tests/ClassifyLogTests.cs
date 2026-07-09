using System.Numerics;
using LogShippingService;

namespace LogShippingServiceTests
{
    /// <summary>
    /// Table-driven unit tests for <see cref="LogShipping.ClassifyLog"/> - the pure decision that folds the restore delay,
    /// the log's LSN position relative to the current redo point, and the chain integrity into a single
    /// <see cref="LogShipping.LogRestoreAction"/>.  These pin down the full positional + chain matrix without a SQL Server instance.
    /// </summary>
    [TestClass]
    public class ClassifyLogTests
    {
        private static readonly DateTime Reference = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime SameIncarnation = Reference.AddDays(-30);
        private static readonly DateTime NewerIncarnation = Reference.AddDays(1);
        private const long Redo = 200;

        private static BackupHeader Header(
            long firstLSN,
            long lastLSN,
            DateTime? backupFinishDate = null,
            DateTime? databaseCreationDate = null,
            bool beginsLogChain = false)
        {
            return new BackupHeader
            {
                BackupFinishDate = backupFinishDate ?? Reference,          // default: not newer than reference (chain guard => Ok)
                DatabaseCreationDate = databaseCreationDate ?? SameIncarnation,
                BeginsLogChain = beginsLogChain,
                FirstLSN = new BigInteger(firstLSN),
                LastLSN = new BigInteger(lastLSN)
            };
        }

        private static LogShipping.LogRestoreAction Classify(
            BackupHeader header,
            DateTime? currentChainCreationDate,
            BigInteger? redoStart = null,
            int restoreDelayMins = 0)
        {
            return LogShipping.ClassifyLog(header, currentChainCreationDate, Reference, redoStart ?? new BigInteger(Redo), Reference, restoreDelayMins);
        }

        // ----- Restore delay (takes precedence over everything) -----

        [TestMethod]
        public void WithinRestoreDelay_IsWaitDelay()
        {
            var header = Header(100, 250, backupFinishDate: Reference.AddMinutes(-5)); // finished 5 min before "now" (Reference)
            var action = LogShipping.ClassifyLog(header, SameIncarnation, Reference, new BigInteger(Redo), now: Reference, restoreDelayMins: 10);
            Assert.AreEqual(LogShipping.LogRestoreAction.WaitDelay, action);
        }

        [TestMethod]
        public void DelayElapsed_IsNotWaitDelay()
        {
            var header = Header(100, 250, backupFinishDate: Reference.AddMinutes(-20));
            var action = LogShipping.ClassifyLog(header, SameIncarnation, Reference, new BigInteger(Redo), now: Reference, restoreDelayMins: 10);
            Assert.AreEqual(LogShipping.LogRestoreAction.Apply, action);
        }

        // ----- Position: LastRestored (FirstLSN <= redo && LastLSN == redo) -----

        [TestMethod]
        public void ExactTip_IsLastRestored()
        {
            var action = Classify(Header(100, Redo), SameIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.LastRestored, action);
        }

        [TestMethod]
        public void ExactTip_NewerIncarnation_IsSourceRecreated()
        {
            var header = Header(100, Redo, backupFinishDate: Reference.AddMinutes(10), databaseCreationDate: NewerIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.SourceRecreated, Classify(header, SameIncarnation));
        }

        // ----- Position: Apply (FirstLSN <= redo && LastLSN > redo) -----

        [TestMethod]
        public void SpansRedoPoint_IsApply()
        {
            var action = Classify(Header(100, 250), SameIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.Apply, action);
        }

        [TestMethod]
        public void SpansRedoPoint_NewerIncarnation_IsSourceRecreated()
        {
            var header = Header(100, 250, backupFinishDate: Reference.AddMinutes(10), databaseCreationDate: NewerIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.SourceRecreated, Classify(header, SameIncarnation));
        }

        [TestMethod]
        public void SpansRedoPoint_BrokenChainDoesNotDivert_StaysApply()
        {
            // A "Broken" chain status must NOT divert a spanning log (preserves original behaviour - only Recreated diverts here).
            var header = Header(100, 250, backupFinishDate: Reference.AddMinutes(10), beginsLogChain: true);
            Assert.AreEqual(LogShipping.LogRestoreAction.Apply, Classify(header, currentChainCreationDate: null));
        }

        // ----- Position: TooOld (FirstLSN < redo) -----

        [TestMethod]
        public void EntirelyBeforeRedoPoint_IsTooOld()
        {
            var action = Classify(Header(150, 180), SameIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.TooOld, action);
        }

        [TestMethod]
        public void TooOld_BrokenChain_IsChainBroken()
        {
            var header = Header(150, 180, backupFinishDate: Reference.AddMinutes(10), beginsLogChain: true);
            Assert.AreEqual(LogShipping.LogRestoreAction.ChainBroken, Classify(header, currentChainCreationDate: null));
        }

        [TestMethod]
        public void TooOld_NewerIncarnation_IsSourceRecreated()
        {
            var header = Header(150, 180, backupFinishDate: Reference.AddMinutes(10), databaseCreationDate: NewerIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.SourceRecreated, Classify(header, SameIncarnation));
        }

        // ----- Position: TooRecent (FirstLSN > redo) -----

        [TestMethod]
        public void EntirelyAfterRedoPoint_IsTooRecent()
        {
            var action = Classify(Header(300, 400), SameIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.TooRecent, action);
        }

        [TestMethod]
        public void TooRecent_BeginsNewChainAhead_IsChainBroken()
        {
            var header = Header(300, 400, backupFinishDate: Reference.AddMinutes(10), beginsLogChain: true);
            Assert.AreEqual(LogShipping.LogRestoreAction.ChainBroken, Classify(header, SameIncarnation));
        }

        [TestMethod]
        public void TooRecent_NewerIncarnation_IsSourceRecreated()
        {
            var header = Header(300, 400, backupFinishDate: Reference.AddMinutes(10), databaseCreationDate: NewerIncarnation);
            Assert.AreEqual(LogShipping.LogRestoreAction.SourceRecreated, Classify(header, SameIncarnation));
        }

        // ----- No redo point yet (redoStart == null) -----

        [TestMethod]
        public void NullRedoStart_IsApply()
        {
            var action = LogShipping.ClassifyLog(Header(100, 250), SameIncarnation, Reference, redoStart: null, now: Reference, restoreDelayMins: 0);
            Assert.AreEqual(LogShipping.LogRestoreAction.Apply, action);
        }
    }
}
