using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Logging;
using Sparrow.Platform.Posix;
using Sparrow.Utils;
using Voron.Data;
using Voron.Global;
using Voron.Impl;
using Voron.Impl.Paging;

namespace Voron.Platform.Posix
{
    public unsafe class Posix32BitsMemoryMapPager : PosixAbstractPager
    {
        private readonly StorageEnvironmentOptions _options;
        private int _fd;
        private readonly bool _isSyncDirAllowed;
        private readonly bool _copyOnWriteMode;

        private readonly ConcurrentDictionary<long, ConcurrentSet<MappedAddresses>> _globalMapping = new ConcurrentDictionary<long, ConcurrentSet<MappedAddresses>>(NumericEqualityComparer.Instance);
        private long _totalMapped;
        private int _concurrentTransactions;
        private readonly ReaderWriterLockSlim _globalMemory = new ReaderWriterLockSlim();

        private long _totalAllocationSize;
        public const int AllocationGranularity = 64 * Constants.Size.Kilobyte;
        private const int NumberOfPagesInAllocationGranularity = AllocationGranularity / Constants.Storage.PageSize;
        public override long TotalAllocationSize => _totalAllocationSize;

        public Posix32BitsMemoryMapPager(StorageEnvironmentOptions options, string file, long? initialFileSize = null,
            bool usePageProtection = false) : base(options, usePageProtection)
        {
            _options = options;
            FileName = file;

            if (Options.CopyOnWriteMode)
                ThrowNotSupportedOption(file);

            _copyOnWriteMode = options.CopyOnWriteMode && file.EndsWith(Constants.DatabaseFilename);
            _isSyncDirAllowed = PosixHelper.CheckSyncDirectoryAllowed(FileName);

            PosixHelper.EnsurePathExists(FileName);

            _fd = Syscall.open(file, OpenFlags.O_RDWR | OpenFlags.O_CREAT | PerPlatformValues.OpenFlags.O_LARGEFILE,
                              FilePermissions.S_IWUSR | FilePermissions.S_IRUSR);
            if (_fd == -1)
            {
                var err = Marshal.GetLastWin32Error();
                PosixHelper.ThrowLastError(err, "when opening " + file);
            }

            _totalAllocationSize = GetFileSize();

            if (_totalAllocationSize == 0 && initialFileSize.HasValue)
            {
                _totalAllocationSize = NearestSizeToAllocationGranularity(initialFileSize.Value);
            }
            if (_totalAllocationSize == 0 || _totalAllocationSize % AllocationGranularity != 0 ||
                _totalAllocationSize != GetFileSize())
            {
                _totalAllocationSize = NearestSizeToAllocationGranularity(_totalAllocationSize);
                PosixHelper.AllocateFileSpace(_options, _fd, _totalAllocationSize, file);
            }

            if (_isSyncDirAllowed && PosixHelper.SyncDirectory(file) == -1)
            {
                var err = Marshal.GetLastWin32Error();
                PosixHelper.ThrowLastError(err, "sync dir for " + file);
            }

            NumberOfAllocatedPages = _totalAllocationSize / Constants.Storage.PageSize +
                                   ((_totalAllocationSize % Constants.Storage.PageSize) == 0 ? 0 : 1);

            SetPagerState(new PagerState(this)
            {
                Files = null,
                MapBase = null,
                AllocationInfos = new PagerState.AllocationInfo[0]
            });
        }

        private static void ThrowNotSupportedOption(string file)
        {
            throw new NotSupportedException(
                "CopyOnWriteMode is currently not supported for 32 bits, error on " +
                file);
        }

        private long NearestSizeToAllocationGranularity(long size)
        {
            var modulos = size % AllocationGranularity;
            if (modulos == 0)
                return Math.Max(size, AllocationGranularity);

            return (size / AllocationGranularity + 1) * AllocationGranularity;
        }

        private long GetFileSize()
        {
            FileInfo fi = new FileInfo(FileName);
            return fi.Length;
        }

        public override bool EnsureMapped(IPagerLevelTransactionState tx, long pageNumber, int numberOfPages)
        {
            var distanceFromStart = (pageNumber % NumberOfPagesInAllocationGranularity);

            var allocationStartPosition = pageNumber - distanceFromStart;

            var state = GetTransactionState(tx);

            LoadedPage page;
            if (state.LoadedPages.TryGetValue(allocationStartPosition, out page))
            {
                if (distanceFromStart + numberOfPages < page.NumberOfPages)
                    return false; // already mapped large enough here
            }

            var ammountToMapInBytes = NearestSizeToAllocationGranularity((distanceFromStart + numberOfPages) * Constants.Storage.PageSize);
            MapPages(state, allocationStartPosition, ammountToMapInBytes);
            return true;
        }

        public override int CopyPage(I4KbBatchWrites destI4KbBatchWrites, long pageNumber, PagerState pagerState)
        {
            long sizeToMap = AllocationGranularity;
            var distanceFromStart = (pageNumber % NumberOfPagesInAllocationGranularity);
            var allocationStartPosition = pageNumber - distanceFromStart;

            var offset = allocationStartPosition * Constants.Storage.PageSize;
            var mmflags = _copyOnWriteMode ? MmapFlags.MAP_PRIVATE : MmapFlags.MAP_SHARED;

            var result = Syscall.mmap64(IntPtr.Zero, (UIntPtr)sizeToMap,
                                                      MmapProts.PROT_READ | MmapProts.PROT_WRITE,
                                                      mmflags, _fd, offset);
            try
            {
                if (result.ToInt64() == -1) //system didn't succeed in mapping the address where we wanted
                {
                    var err = Marshal.GetLastWin32Error();
                    PosixHelper.ThrowLastError(err,
                        $"Unable to map (default view size) {sizeToMap/Constants.Size.Kilobyte:#,#} kb for page {pageNumber} starting at {allocationStartPosition} on {FileName}");
                }

                var pageHeader = (PageHeader*) (result.ToInt64() + distanceFromStart*Constants.Storage.PageSize);

                int numberOfPages = 1;
                if ((pageHeader->Flags & PageFlags.Overflow) == PageFlags.Overflow)
                {
                    numberOfPages = this.GetNumberOfOverflowPages(pageHeader->OverflowSize);
                }


                if (numberOfPages + distanceFromStart > NumberOfPagesInAllocationGranularity)
                {
                    Syscall.munmap(result, (UIntPtr) sizeToMap);
                    result = new IntPtr(-1);
                    sizeToMap =NearestSizeToAllocationGranularity((numberOfPages + distanceFromStart)*
                                                           Constants.Storage.PageSize);
                    result = Syscall.mmap64(IntPtr.Zero, (UIntPtr) sizeToMap, MmapProts.PROT_READ | MmapProts.PROT_WRITE,
                        mmflags, _fd, offset);

                    if (result.ToInt64() == -1)
                    {
                        var err = Marshal.GetLastWin32Error();
                        PosixHelper.ThrowLastError(err,
                            $"Unable to map {sizeToMap/Constants.Size.Kilobyte:#,#} kb for page {pageNumber} starting at {allocationStartPosition} on {FileName}");
                    }

                    pageHeader = (PageHeader*) (result.ToInt64() + (distanceFromStart*Constants.Storage.PageSize));
                }
                const int adjustPageSize = (Constants.Storage.PageSize)/(4*Constants.Size.Kilobyte);

                destI4KbBatchWrites.Write(pageHeader->PageNumber*adjustPageSize, numberOfPages*adjustPageSize,
                    (byte*) pageHeader);

                return numberOfPages;
            }
            finally
            {
                if (result.ToInt64() != -1)
                    Syscall.munmap(result, (UIntPtr) sizeToMap);

            }
        }

        public override I4KbBatchWrites BatchWriter()
        {
            return new Posix32Bit4KbBatchWrites(this);
        }

        public override byte* AcquirePagePointer(IPagerLevelTransactionState tx, long pageNumber, PagerState pagerState = null)
        {
            if (Disposed)
                ThrowAlreadyDisposedException();

            if (pageNumber > NumberOfAllocatedPages || pageNumber < 0)
                ThrowOnInvalidPageNumber(pageNumber, tx.Environment);

            var state = GetTransactionState(tx);

            var distanceFromStart = (pageNumber % NumberOfPagesInAllocationGranularity);
            var allocationStartPosition = pageNumber - distanceFromStart;

            LoadedPage page;
            if (state.LoadedPages.TryGetValue(allocationStartPosition, out page))
            {
                return page.Pointer + (distanceFromStart * Constants.Storage.PageSize);
            }

            page = MapPages(state, allocationStartPosition, AllocationGranularity);
            return page.Pointer + (distanceFromStart * Constants.Storage.PageSize);
        }
        
        private LoadedPage MapPages(TransactionState state, long startPage, long size)
        {
            _globalMemory.EnterReadLock();
            try
            {
                var addresses = _globalMapping.GetOrAdd(startPage,
                    _ => new ConcurrentSet<MappedAddresses>());
                foreach (var addr in addresses)
                {
                    if (addr.Size < size)
                        continue;

                    Interlocked.Increment(ref addr.Usages);
                    return AddMappingToTransaction(state, startPage, size, addr);
                }

                var offset = startPage*Constants.Storage.PageSize;

                if (offset + size > _totalAllocationSize)
                {
                    ThrowInvalidMappingRequested(startPage, size);
                }
                var mmflags = _copyOnWriteMode ? MmapFlags.MAP_PRIVATE : MmapFlags.MAP_SHARED;

                var startingBaseAddressPtr = Syscall.mmap64(IntPtr.Zero, (UIntPtr) size,
                    MmapProts.PROT_READ | MmapProts.PROT_WRITE,
                    mmflags, _fd, offset);

                if (startingBaseAddressPtr.ToInt64() == -1)
                    //system didn't succeed in mapping the address where we wanted
                {
                    var err = Marshal.GetLastWin32Error();

                    PosixHelper.ThrowLastError(err,
                        $"Unable to map {size/Constants.Size.Kilobyte:#,#} kb starting at {startPage} on {FileName}");
                }

                NativeMemory.RegisterFileMapping(FileName, startingBaseAddressPtr, size);

                Interlocked.Add(ref _totalMapped, size);
                var mappedAddresses = new MappedAddresses
                {
                    Address = startingBaseAddressPtr,
                    File = FileName,
                    Size = size,
                    StartPage = startPage,
                    Usages = 1
                };
                addresses.Add(mappedAddresses);
                return AddMappingToTransaction(state, startPage, size, mappedAddresses);
            }
            finally
            {
                _globalMemory.ExitReadLock();
            }
        }

        private LoadedPage AddMappingToTransaction(TransactionState state, long startPage, long size, MappedAddresses mappedAddresses)
        {
            state.TotalLoadedSize += size;
            state.AddressesToUnload.Add(mappedAddresses);
            var loadedPage = new LoadedPage
            {
                Pointer = (byte*)mappedAddresses.Address,
                NumberOfPages = (int)(size / Constants.Storage.PageSize),
                StartPage = startPage
            };
            state.LoadedPages[startPage] = loadedPage;
            return loadedPage;
        }

        private void ThrowInvalidMappingRequested(long startPage, long size)
        {
            throw new InvalidOperationException(
                $"Was asked to map page {startPage} + {size / 1024:#,#} kb, but the file size is only {_totalAllocationSize}, can't do that.");
        }

        private TransactionState GetTransactionState(IPagerLevelTransactionState tx)
        {
            TransactionState transactionState;
            if (tx.PagerTransactionState32Bits == null)
            {
                Interlocked.Increment(ref _concurrentTransactions);
                transactionState = new TransactionState();
                tx.PagerTransactionState32Bits = new Dictionary<AbstractPager, TransactionState>
                {
                    {this, transactionState}
                };
                tx.OnDispose += TxOnOnDispose;
                return transactionState;
            }

            if (tx.PagerTransactionState32Bits.TryGetValue(this, out transactionState) == false)
            {
                Interlocked.Increment(ref _concurrentTransactions);
                transactionState = new TransactionState();
                tx.PagerTransactionState32Bits[this] = transactionState;
                tx.OnDispose += TxOnOnDispose;
            }
            return transactionState;
        }

        private void TxOnOnDispose(IPagerLevelTransactionState lowLevelTransaction)
        {
            if (lowLevelTransaction.PagerTransactionState32Bits == null)
                return;

            TransactionState value;
            if (lowLevelTransaction.PagerTransactionState32Bits.TryGetValue(this, out value) == false)
                return; // nothing mapped here

            lowLevelTransaction.PagerTransactionState32Bits.Remove(this);

            var canCleanup = false;
            foreach (var addr in value.AddressesToUnload)
            {
                canCleanup |= Interlocked.Decrement(ref addr.Usages) == 0;
            }

            Interlocked.Decrement(ref _concurrentTransactions);

            if (canCleanup == false)
                return;

            CleanupMemory(value);
        }

        private void CleanupMemory(TransactionState txState)
        {
            _globalMemory.EnterWriteLock();
            try
            {
                foreach (var addr in txState.AddressesToUnload)
                {
                    if (addr.Usages != 0)
                        continue;

                    ConcurrentSet<MappedAddresses> set;
                    if (!_globalMapping.TryGetValue(addr.StartPage, out set))
                        continue;

                    if (!set.TryRemove(addr))
                        continue;

                    Interlocked.Add(ref _totalMapped, -addr.Size);
                    Syscall.munmap(addr.Address, (UIntPtr)addr.Size);
                    NativeMemory.UnregisterFileMapping(addr.File, addr.Address, addr.Size);

                    if (set.Count == 0)
                    {
                        _globalMapping.TryRemove(addr.StartPage, out set);
                    }
                }
            }
            finally
            {
                _globalMemory.ExitWriteLock();
            }
        }

        private class Posix32Bit4KbBatchWrites : I4KbBatchWrites
        {
            private readonly Posix32BitsMemoryMapPager _parent;
            private readonly TransactionState _state = new TransactionState();

            public Posix32Bit4KbBatchWrites(Posix32BitsMemoryMapPager parent)
            {
                _parent = parent;
            }

            public void Write(long posBy4Kbs, int numberOf4Kbs, byte* source)
            {
                const int pageSizeTo4KbRatio = (Constants.Storage.PageSize / (4 * Constants.Size.Kilobyte));
                var pageNumber = posBy4Kbs / pageSizeTo4KbRatio;
                var offsetBy4Kb = posBy4Kbs % pageSizeTo4KbRatio;
                var numberOfPages = numberOf4Kbs / pageSizeTo4KbRatio;
                if (posBy4Kbs % pageSizeTo4KbRatio != 0 ||
                    numberOf4Kbs % pageSizeTo4KbRatio != 0)
                    numberOfPages++;

                _parent.EnsureContinuous(pageNumber, numberOfPages);

                var distanceFromStart = (pageNumber % NumberOfPagesInAllocationGranularity);
                var allocationStartPosition = pageNumber - distanceFromStart;

                var ammountToMapInBytes = _parent.NearestSizeToAllocationGranularity((distanceFromStart + numberOfPages) * Constants.Storage.PageSize);

                LoadedPage page;
                if (_state.LoadedPages.TryGetValue(allocationStartPosition, out page))
                {
                    if (page.NumberOfPages < distanceFromStart + numberOfPages)
                    {
                        page = _parent.MapPages(_state, allocationStartPosition, ammountToMapInBytes);
                    }
                }
                else
                {
                    page = _parent.MapPages(_state, allocationStartPosition, ammountToMapInBytes);
                }

                var toWrite = numberOf4Kbs * 4 * Constants.Size.Kilobyte;
                byte* destination = page.Pointer +
                                   (distanceFromStart * Constants.Storage.PageSize) +
                                   offsetBy4Kb * (4 * Constants.Size.Kilobyte);

                _parent.UnprotectPageRange(destination, (ulong)toWrite);

                Memory.BulkCopy(destination, source, toWrite);

                _parent.ProtectPageRange(destination, (ulong)toWrite);
            }

            public void Dispose()
            {
                foreach (var page in _state.LoadedPages)
                {
                    var loadedPage = page.Value;
                    // we have to do this here because we need to verify that this is actually written to disk
                    // afterward, we can call flush on this
                    var result = Syscall.msync(new IntPtr(loadedPage.Pointer), (UIntPtr)(loadedPage.NumberOfPages * Constants.Storage.PageSize), MsyncFlags.MS_SYNC);
                    if (result == -1)
                    {
                        var err = Marshal.GetLastWin32Error();
                        PosixHelper.ThrowLastError(err, "msync on " + loadedPage);
                    }
                }

                var canCleanup = false;
                foreach (var addr in _state.AddressesToUnload)
                {
                    canCleanup |= Interlocked.Decrement(ref addr.Usages) == 0;
                }
                if (canCleanup)
                    _parent.CleanupMemory(_state);
            }
        }

        public override void Sync(long totalUnsynced)
        {
            using (var metric = Options.IoMetrics.MeterIoRate(FileName, IoMetrics.MeterType.DataSync, 0))
            {
                metric.IncrementSize(totalUnsynced);
                metric.IncrementFileSize(_totalAllocationSize);

                if (Syscall.fsync(_fd) == -1)
                {
                    var err = Marshal.GetLastWin32Error();
                    PosixHelper.ThrowLastError(err, "fsync " + FileName);
                }
            }
        }

        protected override string GetSourceName()
        {
            return "mmap: " + _fd + " " + FileName;
        }

        protected override PagerState AllocateMorePages(long newLength)
        {
            var newLengthAfterAdjustment = NearestSizeToAllocationGranularity(newLength);

            if (newLengthAfterAdjustment <= _totalAllocationSize)
                return null;

            var allocationSize = newLengthAfterAdjustment - _totalAllocationSize;

            PosixHelper.AllocateFileSpace(_options, _fd, _totalAllocationSize + allocationSize, FileName);

            if (_isSyncDirAllowed && PosixHelper.SyncDirectory(FileName) == -1)
            {
                var err = Marshal.GetLastWin32Error();
                PosixHelper.ThrowLastError(err);
            }

            _totalAllocationSize += allocationSize;
            NumberOfAllocatedPages = _totalAllocationSize / Constants.Storage.PageSize;

            return null;
        }

        public override string ToString()
        {
            return FileName;
        }

        public override void ReleaseAllocationInfo(byte* baseAddress, long size)
        {
            // this isn't actually ever called here, we don't have a global 
            // memory map, only per transaction
        }

        public override void TryPrefetchingWholeFile()
        {
            // we never want to do this, we'll rely on the OS to do it for us
        }

        public override void MaybePrefetchMemory(List<long> pagesToPrefetch)
        {
            // we never want to do this here
        }

        public override void Dispose()
        {
            base.Dispose();
            if (_fd != -1)
            {
                Syscall.close(_fd);
                _fd = -1;
            }
        }
    }
}