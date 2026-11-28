using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class MediaClassifierTests
{
    [Theory]
    [InlineData(true, StorageBus.Sata, 0, "ST2000DM008-2FR102", MediaKind.Hdd)]
    [InlineData(false, StorageBus.Sata, 0, "Samsung SSD 870 EVO", MediaKind.Ssd)]
    [InlineData(false, StorageBus.Sata, 0, "CT1000MX500", MediaKind.Ssd)]
    [InlineData(true, StorageBus.Usb, 4800, "WD My Passport 2626", MediaKind.Hdd)]
    [InlineData(false, StorageBus.Usb, 5400, "Generic USB-SATA bridge", MediaKind.Hdd)]
    [InlineData(null, StorageBus.Usb, 7200, "JMicron bridge", MediaKind.Hdd)]
    [InlineData(null, StorageBus.Usb, 0, "JMicron bridge", MediaKind.Unknown)]
    [InlineData(null, StorageBus.Unknown, 0, "", MediaKind.Unknown)]
    [InlineData(true, StorageBus.Usb, 1, "ASMedia bridge", MediaKind.Ssd)]
    [InlineData(true, StorageBus.Usb, 0, "Samsung Portable SSD T7", MediaKind.Ssd)]
    [InlineData(true, StorageBus.Usb, 0, "Realtek RTL9210 NVME", MediaKind.Ssd)]
    [InlineData(true, StorageBus.Nvme, 7200, "SKHynix_HFS512GEJ9X115N", MediaKind.Ssd)]
    [InlineData(null, StorageBus.Nvme, 0, "", MediaKind.Ssd)]
    [InlineData(true, StorageBus.Sd, 0, "SD card reader", MediaKind.Ssd)]
    [InlineData(false, StorageBus.Sata, 7200, "WDC WD40EZAZ", MediaKind.Hdd)]
    [InlineData(true, StorageBus.Sata, 0, "ST1000LX015 SSHD", MediaKind.Hdd)]
    public void Media_is_classified_from_all_the_evidence(bool? seekPenalty, StorageBus bus, int rotationRate, string device, MediaKind expected)
    {
        Assert.Equal(expected, MediaClassifier.Classify(new MediaEvidence(seekPenalty, bus, rotationRate, device)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0x0400, false)]
    [InlineData(0x0401, true)]
    [InlineData(4800, true)]
    [InlineData(15000, true)]
    [InlineData(0xFFFE, true)]
    [InlineData(0xFFFF, false)]
    public void Only_a_real_spindle_speed_counts_as_rotation(int rate, bool expected)
    {
        Assert.Equal(expected, MediaClassifier.IsRpm(rate));
    }

    [Theory]
    [InlineData(true, StorageBus.Sata, false)]
    [InlineData(false, StorageBus.Sata, true)]
    [InlineData(null, StorageBus.Sata, true)]
    [InlineData(true, StorageBus.Usb, true)]
    [InlineData(true, StorageBus.Ieee1394, true)]
    [InlineData(true, StorageBus.Unknown, true)]
    [InlineData(null, StorageBus.Nvme, false)]
    [InlineData(false, StorageBus.Sd, false)]
    public void The_rotation_rate_is_asked_only_when_the_seek_penalty_is_not_enough(bool? seekPenalty, StorageBus bus, bool expected)
    {
        Assert.Equal(expected, MediaClassifier.WantsRotationRate(new MediaEvidence(seekPenalty, bus, MediaClassifier.RotationNotReported, string.Empty)));
    }

    [Fact]
    public void A_hard_disk_behind_a_bridge_that_hides_its_seek_penalty_can_still_be_defragmented()
    {
        MediaKind media = MediaClassifier.Classify(new MediaEvidence(false, StorageBus.Usb, 5400, "USB bridge"));
        var volume = new DriveVolume('E', "Backup", "NTFS", 4096, 1000, 4_096_000, 2_048_000, media, false, StorageBus.Usb, 5400);
        Assert.Equal(DriveAction.Defragment, volume.DefaultAction);
        DriveDefragmenter.Guard(volume);
    }

    [Fact]
    public void A_solid_state_drive_stays_refused_whatever_its_bridge_claims()
    {
        foreach (MediaEvidence evidence in (MediaEvidence[])[
            new(true, StorageBus.Usb, 1, "bridge"),
            new(true, StorageBus.Nvme, 7200, "NVMe drive"),
            new(true, StorageBus.Usb, 0, "Portable SSD")])
        {
            var volume = new DriveVolume('F', "Fast", "NTFS", 4096, 1000, 4_096_000, 2_048_000, MediaClassifier.Classify(evidence), true, evidence.Bus, evidence.RotationRate);
            Assert.Equal(DriveAction.Trim, volume.DefaultAction);
            Assert.Throws<DriveRelocationRefusedException>(() => DriveDefragmenter.Guard(volume));
        }
    }

    [Fact]
    public void The_fixed_volumes_of_this_machine_keep_the_solid_state_guard()
    {
        foreach (DriveVolume volume in SystemVolumes.Instance.FixedVolumes())
        {
            if (MediaClassifier.IsFlashBus(volume.Bus) || volume.RotationRate == MediaClassifier.NonRotating)
            {
                Assert.Equal(MediaKind.Ssd, volume.Media);
            }

            if (volume.Media == MediaKind.Ssd)
            {
                Assert.False(volume.CanRelocate);
                Assert.Throws<DriveRelocationRefusedException>(() => DriveDefragmenter.Guard(volume));
            }

            if (MediaClassifier.IsRpm(volume.RotationRate))
            {
                Assert.Equal(MediaKind.Hdd, volume.Media);
            }
        }
    }

    [Fact]
    public void A_drive_with_no_media_evidence_is_only_analyzed()
    {
        var volume = new DriveVolume('G', "Unknown", "NTFS", 4096, 1000, 4_096_000, 2_048_000, MediaClassifier.Classify(MediaEvidence.None), false);
        Assert.Equal(DriveAction.AnalyzeOnly, volume.DefaultAction);
        Assert.Throws<DriveRelocationRefusedException>(() => DriveDefragmenter.Guard(volume));
    }
}
