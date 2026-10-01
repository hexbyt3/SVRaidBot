using PKHeX.Core;
using RaidCrawler.Core.Structures;
using System;
using System.Collections.Generic;
using SysBot.Base;
using System.Threading;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Helpers;
/// <summary>
/// Initializes a new instance of the RaidMemoryManager with the necessary memory pointers.
/// </summary>
/// <param name="connection">The Switch connection used for memory operations</param>
/// <param name="raidBlockPointerP">Memory pointer to Paldea raid data</param>
/// <param name="raidBlockPointerK">Memory pointer to Kitakami raid data</param>
/// <param name="raidBlockPointerB">Memory pointer to Blueberry raid data</param>
public class RaidMemoryManager(ISwitchConnectionAsync connection, ulong raidBlockPointerBase, ulong raidBlockPointerKitakami, ulong raidBlockPointerBlueberry)
{
    private readonly ISwitchConnectionAsync _connection = connection;
    private readonly ulong _raidBlockPointerBase = raidBlockPointerBase;
    private readonly ulong _raidBlockPointerKitakami = raidBlockPointerKitakami;
    private readonly ulong _raidBlockPointerBlueberry = raidBlockPointerBlueberry;

    // One number per raid slot across all three maps. Each map gets its whole
    // block (Paldea 72 slots, Kitakami 100, Blueberry 80) so numbers never
    // overlap: Paldea 0-71, Kitakami 72-171, Blueberry 172-251.
    public const int KitakamiStartIndex = (int)RaidBlock.MAX_COUNT_BASE;
    public const int BlueberryStartIndex = KitakamiStartIndex + (int)RaidBlock.MAX_COUNT_KITAKAMI;
    public const int TotalSlots = BlueberryStartIndex + (int)RaidBlock.MAX_COUNT_BLUEBERRY;

    // Within each raid entry the seed sits 0x10 bytes in.
    private const int SeedOffsetInRaid = 0x10;

    public static int ToGlobalIndex(TeraRaidMapParent map, int slot) => map switch
    {
        TeraRaidMapParent.Kitakami => KitakamiStartIndex + slot,
        TeraRaidMapParent.Blueberry => BlueberryStartIndex + slot,
        _ => slot,
    };

    public static (TeraRaidMapParent Map, int Slot) FromGlobalIndex(int index)
    {
        if (index < 0 || index >= TotalSlots)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a raid slot.");
        if (index < KitakamiStartIndex)
            return (TeraRaidMapParent.Paldea, index);
        if (index < BlueberryStartIndex)
            return (TeraRaidMapParent.Kitakami, index - KitakamiStartIndex);
        return (TeraRaidMapParent.Blueberry, index - BlueberryStartIndex);
    }

    /// <summary>
    /// Offset of a slot's seed from the start of its map's raid entries
    /// (the data <see cref="ReadRaidData"/> returns).
    /// </summary>
    public static int SeedOffset(int slot) => slot * (int)Raid.SIZE + SeedOffsetInRaid;

    /// <summary>
    /// Reads raid data for the specified map region.
    /// </summary>
    /// <param name="mapType">The region to read raid data from (Paldea, Kitakami, or Blueberry)</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>Byte array containing raw raid data</returns>
    /// <exception cref="ArgumentException">Thrown when an invalid region is specified</exception>
    public async Task<byte[]> ReadRaidData(TeraRaidMapParent mapType, CancellationToken token)
    {
        return mapType switch
        {
            TeraRaidMapParent.Paldea => await _connection.ReadBytesAbsoluteAsync(_raidBlockPointerBase + RaidBlock.HEADER_SIZE, (int)RaidBlock.SIZE_BASE, token).ConfigureAwait(false),
            TeraRaidMapParent.Kitakami => await _connection.ReadBytesAbsoluteAsync(_raidBlockPointerKitakami, (int)RaidBlock.SIZE_KITAKAMI, token).ConfigureAwait(false),
            TeraRaidMapParent.Blueberry => await _connection.ReadBytesAbsoluteAsync(_raidBlockPointerBlueberry, (int)RaidBlock.SIZE_BLUEBERRY, token).ConfigureAwait(false),
            _ => throw new ArgumentException("Invalid region", nameof(mapType))
        };
    }

    /// <summary>
    /// Reads the seed value at a specific raid index.
    /// </summary>
    /// <param name="index">The global raid index</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>The seed value at the specified index, or 0 if the raid is completed</returns>
    /// <summary>
    /// Reads the seed value at a specific raid index.
    /// </summary>
    /// <param name="index">The global raid index</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>The seed value at the specified index, or 0 if the raid is completed</returns>
    public async Task<uint> ReadSeedAtIndex(int index, CancellationToken token)
    {
        var (map, slot) = FromGlobalIndex(index);
        ulong entries = map switch
        {
            TeraRaidMapParent.Kitakami => _raidBlockPointerKitakami,
            TeraRaidMapParent.Blueberry => _raidBlockPointerBlueberry,
            _ => _raidBlockPointerBase + RaidBlock.HEADER_SIZE,
        };
        var data = await _connection.ReadBytesAbsoluteAsync(entries + (ulong)SeedOffset(slot), 4, token).ConfigureAwait(false);
        return BitConverter.ToUInt32(data, 0);
    }

    /// <summary>
    /// Injects a raid seed and crystal type at the specified index in memory.
    /// </summary>
    /// <param name="index">Index location to inject the seed</param>
    /// <param name="seed">Raid seed value</param>
    /// <param name="crystalType">Type of crystal (Base, Black, Might, etc.)</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>True if injection was successful, false otherwise</returns>
    public async Task<bool> InjectSeed(int index, uint seed, TeraCrystalType crystalType, CancellationToken token)
    {
        try
        {
            // Get the appropriate pointer
            List<long> ptr = DeterminePointer(index);

            // Inject the seed
            byte[] seedBytes = BitConverter.GetBytes(seed);
            await _connection.PointerPoke(seedBytes, ptr, token).ConfigureAwait(false);

            // Verify seed was written correctly
            byte[] verification = await _connection.PointerPeek(4, ptr, token).ConfigureAwait(false);
            uint verifiedSeed = BitConverter.ToUInt32(verification, 0);

            if (seed != verifiedSeed)
            {
                return false;
            }

            // Inject crystal type
            var crystalPtr = new List<long>(ptr);
            crystalPtr[3] = ptr[3] + 0x08;
            byte[] crystalBytes = BitConverter.GetBytes((int)crystalType);
            await _connection.PointerPoke(crystalBytes, crystalPtr, token).ConfigureAwait(false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines the appropriate memory pointer for a raid at the specified index.
    /// </summary>
    /// <param name="index">Raid index</param>
    /// <returns>List of longs representing the memory pointer</returns>
    private static List<long> DeterminePointer(int index)
    {
        var (map, slot) = FromGlobalIndex(index);
        var chain = map switch
        {
            TeraRaidMapParent.Kitakami => Offsets.RaidBlockPointerKitakami.ToArray(),
            TeraRaidMapParent.Blueberry => Offsets.RaidBlockPointerBlueberry.ToArray(),
            _ => Offsets.RaidBlockPointerBase.ToArray(),
        };
        // Paldea's block starts with a header before the raid entries.
        long entries = chain[^1] + (map == TeraRaidMapParent.Paldea ? RaidBlock.HEADER_SIZE : 0);
        chain[^1] = entries + SeedOffset(slot);
        return [.. chain];
    }

    /// <summary>
    /// Reads the IsActive flag for a raid at the specified index.
    /// IsActive (IsEnabled) is at offset 0x00 within the raid structure.
    /// DeterminePointer returns a pointer to the Seed (offset 0x10), so we subtract 0x10.
    /// </summary>
    /// <param name="index">The global raid index</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>True if the raid is marked as active in memory</returns>
    public async Task<bool> ReadIsActiveFlag(int index, CancellationToken token)
    {
        try
        {
            var ptr = DeterminePointer(index);
            // IsActive is at offset 0x00, Seed is at offset 0x10, so IsActive = Seed - 0x10
            ptr[3] -= 0x10;
            // IsActive is a uint32 (4 bytes), value of 1 = active
            byte[] data = await _connection.PointerPeek(4, ptr, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(data, 0) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the IsActive flag for a raid at the specified index.
    /// IsActive (IsEnabled) is at offset 0x00 within the raid structure.
    /// DeterminePointer returns a pointer to the Seed (offset 0x10), so we subtract 0x10.
    /// </summary>
    /// <param name="index">The global raid index</param>
    /// <param name="isActive">True to mark as active, false to mark as inactive</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>True if the operation was successful</returns>
    public async Task<bool> SetIsActiveFlag(int index, bool isActive, CancellationToken token)
    {
        try
        {
            var ptr = DeterminePointer(index);
            // IsActive is at offset 0x00, Seed is at offset 0x10, so IsActive = Seed - 0x10
            ptr[3] -= 0x10;
            // IsActive is a uint32 (4 bytes), value of 1 = active, 0 = inactive
            byte[] flagBytes = BitConverter.GetBytes(isActive ? 1u : 0u);
            await _connection.PointerPoke(flagBytes, ptr, token).ConfigureAwait(false);

            byte[] verification = await _connection.PointerPeek(4, ptr, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(verification, 0) == (isActive ? 1u : 0u);
        }
        catch
        {
            return false;
        }
    }
}