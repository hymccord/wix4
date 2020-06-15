// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.WindowsInstaller.Bind
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using WixToolset.Core.Bind;
    using WixToolset.Data;
    using WixToolset.Data.Tuples;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// AssignMediaCommand assigns files to cabs based on Media or MediaTemplate rows.
    /// </summary>
    internal class AssignMediaCommand
    {
        private const int DefaultMaximumUncompressedMediaSize = 200; // Default value is 200 MB

        public AssignMediaCommand(IntermediateSection section, IMessaging messaging, IEnumerable<FileFacade> fileFacades, bool compressed)
        {
            this.CabinetNameTemplate = "Cab{0}.cab";
            this.Section = section;
            this.Messaging = messaging;
            this.FileFacades = fileFacades;
            this.FilesCompressed = compressed;
        }

        private IntermediateSection Section { get; }

        private IMessaging Messaging { get; }

        private IEnumerable<FileFacade> FileFacades { get; }

        private bool FilesCompressed { get; }

        private string CabinetNameTemplate { get; set; }

        /// <summary>
        /// Gets cabinets with their file rows.
        /// </summary>
        public Dictionary<MediaTuple, IEnumerable<FileFacade>> FileFacadesByCabinetMedia { get; private set; }

        /// <summary>
        /// Get uncompressed file rows. This will contain file rows of File elements that are marked with compression=no.
        /// This contains all the files when Package element is marked with compression=no
        /// </summary>
        public IEnumerable<FileFacade> UncompressedFileFacades { get; private set; }

        public void Execute()
        {
            var mediaTuples = this.Section.Tuples.OfType<MediaTuple>().ToList();
            var mediaTemplateTuples = this.Section.Tuples.OfType<WixMediaTemplateTuple>().ToList();

            // If both tuples are authored, it is an error.
            if (mediaTemplateTuples.Count > 0 && mediaTuples.Count > 1)
            {
                throw new WixException(ErrorMessages.MediaTableCollision(null));
            }

            // When building merge module, all the files go to "#MergeModule.CABinet".
            if (SectionType.Module == this.Section.Type)
            {
                var mergeModuleMediaTuple = this.Section.AddTuple(new MediaTuple
                {
                    Cabinet = "#MergeModule.CABinet",
                });

                this.FileFacadesByCabinetMedia = new Dictionary<MediaTuple, IEnumerable<FileFacade>>
                {
                    { mergeModuleMediaTuple, this.FileFacades }
                };

                this.UncompressedFileFacades = Array.Empty<FileFacade>();
            }
            else if (mediaTemplateTuples.Count == 0)
            {
                var filesByCabinetMedia = new Dictionary<MediaTuple, List<FileFacade>>();

                var uncompressedFiles = new List<FileFacade>();

                this.ManuallyAssignFiles(mediaTuples, filesByCabinetMedia, uncompressedFiles);

                this.FileFacadesByCabinetMedia = filesByCabinetMedia.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<FileFacade>)kvp.Value);

                this.UncompressedFileFacades = uncompressedFiles;
            }
            else
            {
                var filesByCabinetMedia = new Dictionary<MediaTuple, List<FileFacade>>();

                var uncompressedFiles = new List<FileFacade>();

                this.AutoAssignFiles(mediaTuples, filesByCabinetMedia, uncompressedFiles);

                this.FileFacadesByCabinetMedia = filesByCabinetMedia.ToDictionary(kvp => kvp.Key, kvp => (IEnumerable<FileFacade>)kvp.Value);

                this.UncompressedFileFacades = uncompressedFiles;
            }
        }

        /// <summary>
        /// Assign files to cabinets based on MediaTemplate authoring.
        /// </summary>
        /// <param name="fileFacades">FileRowCollection</param>
        private void AutoAssignFiles(List<MediaTuple> mediaTable, Dictionary<MediaTuple, List<FileFacade>> filesByCabinetMedia, List<FileFacade> uncompressedFiles)
        {
            const int MaxCabIndex = 999;

            ulong currentPreCabSize = 0;
            ulong maxPreCabSizeInBytes;
            var maxPreCabSizeInMB = 0;
            var currentCabIndex = 0;

            MediaTuple currentMediaRow = null;

            var mediaTemplateTable = this.Section.Tuples.OfType<WixMediaTemplateTuple>();

            // Remove all previous media tuples since they will be replaced with
            // media template.
            foreach (var mediaTuple in mediaTable)
            {
                this.Section.Tuples.Remove(mediaTuple);
            }

            // Auto assign files to cabinets based on maximum uncompressed media size
            var mediaTemplateRow = mediaTemplateTable.Single();

            if (!String.IsNullOrEmpty(mediaTemplateRow.CabinetTemplate))
            {
                this.CabinetNameTemplate = mediaTemplateRow.CabinetTemplate;
            }

            var mumsString = Environment.GetEnvironmentVariable("WIX_MUMS");

            try
            {
                // Override authored mums value if environment variable is authored.
                if (!String.IsNullOrEmpty(mumsString))
                {
                    maxPreCabSizeInMB = Int32.Parse(mumsString);
                }
                else
                {
                    maxPreCabSizeInMB = mediaTemplateRow.MaximumUncompressedMediaSize ?? DefaultMaximumUncompressedMediaSize;
                }

                maxPreCabSizeInBytes = (ulong)maxPreCabSizeInMB * 1024 * 1024;
            }
            catch (FormatException)
            {
                throw new WixException(ErrorMessages.IllegalEnvironmentVariable("WIX_MUMS", mumsString));
            }
            catch (OverflowException)
            {
                throw new WixException(ErrorMessages.MaximumUncompressedMediaSizeTooLarge(null, maxPreCabSizeInMB));
            }

            var mediaTuplesByDiskId = new Dictionary<int, MediaTuple>();

            foreach (var facade in this.FileFacades)
            {
                // When building a product, if the current file is not to be compressed or if
                // the package set not to be compressed, don't cab it.
                if (SectionType.Product == this.Section.Type && (facade.Uncompressed || !this.FilesCompressed))
                {
                    uncompressedFiles.Add(facade);
                    continue;
                }

                if (currentCabIndex == MaxCabIndex)
                {
                    // Associate current file with last cab (irrespective of the size) and cab index is not incremented anymore.
                }
                else
                {
                    // Update current cab size.
                    currentPreCabSize += (ulong)facade.FileSize;

                    // Overflow due to current file
                    if (currentPreCabSize > maxPreCabSizeInBytes)
                    {
                        currentMediaRow = this.AddMediaTuple(mediaTemplateRow, ++currentCabIndex);
                        mediaTuplesByDiskId.Add(currentMediaRow.DiskId, currentMediaRow);
                        filesByCabinetMedia.Add(currentMediaRow, new List<FileFacade>());

                        // Now files larger than MaxUncompressedMediaSize will be the only file in its cabinet so as to respect MaxUncompressedMediaSize
                        currentPreCabSize = (ulong)facade.FileSize;
                    }
                    else // file fits in the current cab.
                    {
                        if (currentMediaRow == null)
                        {
                            // Create new cab and MediaRow
                            currentMediaRow = this.AddMediaTuple(mediaTemplateRow, ++currentCabIndex);
                            mediaTuplesByDiskId.Add(currentMediaRow.DiskId, currentMediaRow);
                            filesByCabinetMedia.Add(currentMediaRow, new List<FileFacade>());
                        }
                    }
                }

                // Associate current file with current cab.
                var cabinetFiles = filesByCabinetMedia[currentMediaRow];
                facade.DiskId = currentCabIndex;
                cabinetFiles.Add(facade);
            }

            // If there are uncompressed files and no MediaRow, create a default one.
            if (uncompressedFiles.Count > 0 && !this.Section.Tuples.OfType<MediaTuple>().Any())
            {
                var defaultMediaRow = this.Section.AddTuple(new MediaTuple(null, new Identifier(AccessModifier.Private, 1))
                {
                    DiskId = 1,
                });

                mediaTuplesByDiskId.Add(1, defaultMediaRow);
            }
        }

        /// <summary>
        /// Assign files to cabinets based on Media authoring.
        /// </summary>
        private void ManuallyAssignFiles(List<MediaTuple> mediaTuples, Dictionary<MediaTuple, List<FileFacade>> filesByCabinetMedia, List<FileFacade> uncompressedFiles)
        {
            var mediaTuplesByDiskId = new Dictionary<int, MediaTuple>();

            if (mediaTuples.Any())
            {
                var cabinetMediaTuples = new Dictionary<string, MediaTuple>(StringComparer.OrdinalIgnoreCase);
                foreach (var mediaTuple in mediaTuples)
                {
                    // If the Media row has a cabinet, make sure it is unique across all Media rows.
                    if (!String.IsNullOrEmpty(mediaTuple.Cabinet))
                    {
                        if (cabinetMediaTuples.TryGetValue(mediaTuple.Cabinet, out var existingRow))
                        {
                            this.Messaging.Write(ErrorMessages.DuplicateCabinetName(mediaTuple.SourceLineNumbers, mediaTuple.Cabinet));
                            this.Messaging.Write(ErrorMessages.DuplicateCabinetName2(existingRow.SourceLineNumbers, existingRow.Cabinet));
                        }
                        else
                        {
                            cabinetMediaTuples.Add(mediaTuple.Cabinet, mediaTuple);
                        }

                        filesByCabinetMedia.Add(mediaTuple, new List<FileFacade>());
                    }

                    mediaTuplesByDiskId.Add(mediaTuple.DiskId, mediaTuple);
                }
            }

            foreach (var facade in this.FileFacades)
            {
                if (!mediaTuplesByDiskId.TryGetValue(facade.DiskId, out var mediaTuple))
                {
                    this.Messaging.Write(ErrorMessages.MissingMedia(facade.SourceLineNumber, facade.DiskId));
                    continue;
                }

                // When building a product, if the current file is to be uncompressed or if
                // the package set not to be compressed, don't cab it.
                var compressed = facade.Compressed;
                var uncompressed = facade.Uncompressed;
                if (SectionType.Product == this.Section.Type && (uncompressed || (!compressed && !this.FilesCompressed)))
                {
                    uncompressedFiles.Add(facade);
                }
                else // file is marked compressed.
                {
                    if (filesByCabinetMedia.TryGetValue(mediaTuple, out var cabinetFiles))
                    {
                        cabinetFiles.Add(facade);
                    }
                    else
                    {
                        this.Messaging.Write(ErrorMessages.ExpectedMediaCabinet(facade.SourceLineNumber, facade.Id, facade.DiskId));
                    }
                }
            }
        }

        /// <summary>
        /// Adds a tuple to the section with cab name template filled in.
        /// </summary>
        /// <param name="mediaTable"></param>
        /// <param name="cabIndex"></param>
        /// <returns></returns>
        private MediaTuple AddMediaTuple(WixMediaTemplateTuple mediaTemplateTuple, int cabIndex)
        {
            return this.Section.AddTuple(new MediaTuple(mediaTemplateTuple.SourceLineNumbers, new Identifier(AccessModifier.Private, cabIndex))
            {
                DiskId = cabIndex,
                Cabinet = String.Format(CultureInfo.InvariantCulture, this.CabinetNameTemplate, cabIndex),
                CompressionLevel = mediaTemplateTuple.CompressionLevel,
            });
        }
    }
}
