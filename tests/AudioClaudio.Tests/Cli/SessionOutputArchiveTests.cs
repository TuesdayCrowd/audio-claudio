using System;
using System.IO;
using AudioClaudio.Cli.Composition;
using Xunit;

namespace AudioClaudio.Tests.Cli;

public class SessionOutputArchiveTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "acl_archive_" + Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "Fast")]
    public void CleanLatest_deletes_top_level_output_files_but_leaves_other_extensions_and_subdirectories()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string keep = Path.Combine(dir, "keep.txt");
            string mid = Path.Combine(dir, "raw.mid");
            string xml = Path.Combine(dir, "score.musicxml");
            string wav = Path.Combine(dir, "input.wav");
            string archiveSubdir = Path.Combine(dir, "20260101_0000");
            string archivedMid = Path.Combine(archiveSubdir, "old.mid");
            Directory.CreateDirectory(archiveSubdir);
            File.WriteAllText(keep, "keep");
            File.WriteAllText(mid, "mid");
            File.WriteAllText(xml, "xml");
            File.WriteAllText(wav, "wav");
            File.WriteAllText(archivedMid, "old");

            var deleted = SessionOutputArchive.CleanLatest(dir);

            Assert.Equal(3, deleted.Count);
            Assert.False(File.Exists(mid));
            Assert.False(File.Exists(xml));
            Assert.False(File.Exists(wav));
            Assert.True(File.Exists(keep));       // other extensions survive
            Assert.True(File.Exists(archivedMid)); // prior archive subfolder untouched (non-recursive clean)
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CleanLatest_on_a_missing_directory_returns_empty_and_does_not_throw()
    {
        string dir = NewTempDir(); // deliberately never created

        var deleted = SessionOutputArchive.CleanLatest(dir);

        Assert.Empty(deleted);
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void CleanLatest_also_deletes_a_log_txt_in_the_root()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "log.txt");
            File.WriteAllText(log, "previous take's console output");

            var deleted = SessionOutputArchive.CleanLatest(dir);

            Assert.False(File.Exists(log));
            Assert.Contains(log, deleted);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Archive_copies_top_level_output_files_into_the_timestamp_folder_and_leaves_originals_in_place()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string mid = Path.Combine(dir, "raw.mid");
            string xml = Path.Combine(dir, "score.musicxml");
            string wav = Path.Combine(dir, "input.wav");
            string other = Path.Combine(dir, "notes.txt");
            File.WriteAllText(mid, "mid");
            File.WriteAllText(xml, "xml");
            File.WriteAllText(wav, "wav");
            File.WriteAllText(other, "other");

            string archiveDir = SessionOutputArchive.Archive(dir, "20260708_1334");

            Assert.Equal(Path.Combine(dir, "20260708_1334"), archiveDir);
            foreach (string name in new[] { "raw.mid", "score.musicxml", "input.wav" })
            {
                Assert.True(File.Exists(Path.Combine(dir, name)));         // original stays in the root
                Assert.True(File.Exists(Path.Combine(archiveDir, name)));  // and a copy lands in the archive
            }
            Assert.False(File.Exists(Path.Combine(archiveDir, "notes.txt"))); // non-matching extension not archived
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Archive_does_not_copy_log_txt()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string mid = Path.Combine(dir, "raw.mid");
            string log = Path.Combine(dir, "log.txt");
            File.WriteAllText(mid, "mid");
            File.WriteAllText(log, "console output");

            string archiveDir = SessionOutputArchive.Archive(dir, "20260708_1400");

            Assert.True(File.Exists(Path.Combine(archiveDir, "raw.mid")));
            Assert.False(File.Exists(Path.Combine(archiveDir, "log.txt")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Archive_called_twice_with_the_same_timestamp_overwrites_without_throwing()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            string mid = Path.Combine(dir, "raw.mid");
            File.WriteAllText(mid, "first");
            string archiveDir1 = SessionOutputArchive.Archive(dir, "20260708_1334");

            File.WriteAllText(mid, "second");
            string archiveDir2 = SessionOutputArchive.Archive(dir, "20260708_1334"); // must not throw

            Assert.Equal(archiveDir1, archiveDir2);
            Assert.Equal("second", File.ReadAllText(Path.Combine(archiveDir2, "raw.mid")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
