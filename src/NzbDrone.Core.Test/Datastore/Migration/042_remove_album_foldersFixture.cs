using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class remove_album_foldersFixture : MigrationTest<remove_album_folders>
    {
        private void GivenNamingConfig(remove_album_folders c)
        {
            c.Insert.IntoTable("NamingConfig").Row(new
            {
                Id = 1,
                ReplaceIllegalCharacters = true,
                RenameTracks = true,
                AlbumFolderFormat = "{Album Title} ({Release Year})",
                StandardTrackFormat = "{Artist Name} - {Album Title} - {track:00} - {Track Title}",
                MultiDiscTrackFormat = "{Medium Format} {medium:00}/{Artist Name} - {Album Title} - {track:00} - {Track Title}",
                ArtistFolderFormat = "{Artist Name}",
            });
        }

        private void GivenArtist(remove_album_folders c)
        {
            var artistPath = $"/mnt/data/path/artist".AsOsAgnostic();

            c.Insert.IntoTable("Artists").Row(new
            {
                Id = 1,
                ArtistMetadataId = 1,
                CleanName = "artist",
                Path = artistPath,
                Monitored = 1,
                AlbumFolder = 1,
                MetadataProfileId = 1,
            });
        }

        [Test]
        public void migration_042_should_concat_standard_track_format_with_album_folder_format()
        {
            var db = WithMigrationTestDb(c =>
            {
                GivenNamingConfig(c);
            });

            var namingConfig = db.Query("SELECT * from NamingConfig").First();

            namingConfig.Keys.Should().NotContain("AlbumFolder");
            namingConfig["StandardTrackFormat"].Should().Be("{Album Title} ({Release Year})/{Artist Name} - {Album Title} - {track:00} - {Track Title}");
            namingConfig["MultiDiscTrackFormat"].Should().Be("{Album Title} ({Release Year})/{Medium Format} {medium:00}/{Artist Name} - {Album Title} - {track:00} - {Track Title}");
        }

        [Test]
        public void migration_042_should_remove_album_folder_property_from_artists()
        {
            var db = WithMigrationTestDb(c =>
            {
                GivenArtist(c);
            });

            var artists = db.Query("SELECT * from Artists");

            artists.Select(x => x.Keys).Should().NotContain("AlbumFolder");
        }
    }
}
