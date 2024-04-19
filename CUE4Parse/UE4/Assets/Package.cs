using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.ACE7.Encryption;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Serilog;

namespace CUE4Parse.UE4.Assets
{
    [SkipObjectRegistration]
    public sealed class Package : AbstractUePackage
    {
        public override FPackageFileSummary Summary { get; }
        public override FNameEntrySerialized[] NameMap { get; }
        public FObjectImport[] ImportMap { get; }
        public FObjectExport[] ExportMap { get; }
        public FPackageIndex[][]? DependsMap { get; }
        public FPackageIndex[]? PreloadDependencies { get; }
        public FObjectDataResource[]? DataResourceMap { get; }
        public override Lazy<UObject>[] ExportsLazy => ExportMap.Select(it => it.ExportObject).ToArray();
        public override bool IsFullyLoaded { get; } = false;
        private ExportLoader[] _exportLoaders; // Nonnull if useLazySerialization is false

        public Package(FArchive uasset, FArchive? uexp, Lazy<FArchive?>? ubulk = null, Lazy<FArchive?>? uptnl = null, IFileProvider? provider = null, TypeMappings? mappings = null, bool useLazySerialization = true)
            : base(uasset.Name.SubstringBeforeLast('.'), provider, mappings)
        {
            // We clone the version container because it can be modified with package specific versions when reading the summary
            uasset.Versions = (VersionContainer) uasset.Versions.Clone();
            FAssetArchive uassetAr;
            ACE7XORKey? xorKey = null;
            ACE7Decrypt? decryptor = null;
            if (uasset.Game == EGame.GAME_AceCombat7)
            {
                decryptor = new ACE7Decrypt();
                uassetAr = new FAssetArchive(decryptor.DecryptUassetArchive(uasset, out xorKey), this);
            }
            else uassetAr = new FAssetArchive(uasset, this);
            Summary = new FPackageFileSummary(uassetAr);

            uassetAr.SeekAbsolute(Summary.NameOffset, SeekOrigin.Begin);
            NameMap = new FNameEntrySerialized[Summary.NameCount];
            uassetAr.ReadArray(NameMap, () => new FNameEntrySerialized(uassetAr));

            uassetAr.SeekAbsolute(Summary.ImportOffset, SeekOrigin.Begin);
            ImportMap = new FObjectImport[Summary.ImportCount];
            uassetAr.ReadArray(ImportMap, () => new FObjectImport(uassetAr));

            uassetAr.SeekAbsolute(Summary.ExportOffset, SeekOrigin.Begin);
            ExportMap = new FObjectExport[Summary.ExportCount]; // we need this to get its final size in some case
            uassetAr.ReadArray(ExportMap, () => new FObjectExport(uassetAr));

            if (!useLazySerialization && Summary.DependsOffset > 0 && Summary.ExportCount > 0)
            {
                uassetAr.SeekAbsolute(Summary.DependsOffset, SeekOrigin.Begin);
                DependsMap = uassetAr.ReadArray(Summary.ExportCount, () => uassetAr.ReadArray(() => new FPackageIndex(uassetAr)));
            }

            if (!useLazySerialization && Summary.PreloadDependencyCount > 0 && Summary.PreloadDependencyOffset > 0)
            {
                uassetAr.SeekAbsolute(Summary.PreloadDependencyOffset, SeekOrigin.Begin);
                PreloadDependencies = uassetAr.ReadArray(Summary.PreloadDependencyCount, () => new FPackageIndex(uassetAr));
            }

            if (Summary.DataResourceOffset > 0)
            {
                uassetAr.SeekAbsolute(Summary.DataResourceOffset, SeekOrigin.Begin);
                var dataResourceVersion = (EObjectDataResourceVersion) uassetAr.Read<uint>();
                if (dataResourceVersion > EObjectDataResourceVersion.Invalid && dataResourceVersion <= EObjectDataResourceVersion.Latest)
                {
                    DataResourceMap = uassetAr.ReadArray(() => new FObjectDataResource(uassetAr));
                }
            }

            FAssetArchive uexpAr;
            if (uexp != null)
            {
                if (uasset.Game == EGame.GAME_AceCombat7 && decryptor != null && xorKey != null)
                {
                    uexpAr = new FAssetArchive(decryptor.DecryptUexpArchive(uexp, xorKey), this, (int) uassetAr.Length);
                } else uexpAr = new FAssetArchive(uexp, this, (int) uassetAr.Length);
            }
            else uexpAr = uassetAr;

            if (ubulk != null)
            {
                //var offset = (int) (Summary.TotalHeaderSize + ExportMap.Sum(export => export.SerialSize));
                var offset = Summary.BulkDataStartOffset;
                uexpAr.AddPayload(PayloadType.UBULK, offset, ubulk);
            }

            if (uptnl != null)
            {
                var offset = Summary.BulkDataStartOffset;
                uexpAr.AddPayload(PayloadType.UPTNL, offset, uptnl);
            }

            if (HasFlags(EPackageFlags.PKG_UnversionedProperties) && mappings == null)
                throw new ParserException("Package has unversioned properties but mapping file is missing, can't serialize");

            if (useLazySerialization)
            {
                var rand = new Random();
                foreach (var export in ExportMap)
                {
                    string heuristic = "preserve_detail";
                    //string heuristic = "preserve_structure";
                    bool b_excludeRoof = true;
                    if (b_downsampled(export, rand, heuristic, b_excludeRoof))
                    {
                        export.ExportObject = new Lazy<UObject>();
                        continue;
                    }

                    export.ExportObject = new Lazy<UObject>(() =>
                    {
                        // Create
                        var obj = ConstructObject(ResolvePackageIndex(export.ClassIndex)?.Object?.Value as UStruct);
                        obj.Name = export.ObjectName.Text;
                        obj.Outer = (ResolvePackageIndex(export.OuterIndex) as ResolvedExportObject)?.Object.Value ?? this;
                        obj.Super = ResolvePackageIndex(export.SuperIndex) as ResolvedExportObject;
                        obj.Template = ResolvePackageIndex(export.TemplateIndex) as ResolvedExportObject;
                        obj.Flags |= (EObjectFlags) export.ObjectFlags; // We give loaded objects the RF_WasLoaded flag in ConstructObject, so don't remove it again in here

                        // Serialize
                        var Ar = (FAssetArchive) uexpAr.Clone();
                        Ar.SeekAbsolute(export.SerialOffset, SeekOrigin.Begin);
                        DeserializeObject(obj, Ar, export.SerialSize);
                        // TODO right place ???
                        obj.Flags |= EObjectFlags.RF_LoadCompleted;
                        obj.PostLoad();
                        return obj;
                    });
                }
            }
            else
            {
                _exportLoaders = new ExportLoader[ExportMap.Length];
                for (var i = 0; i < ExportMap.Length; i++)
                {
                    _exportLoaders[i] = new(this, ExportMap[i], uexpAr);
                }
            }

            IsFullyLoaded = true;
        }

        private bool b_downsampled(FObjectExport export, Random rand, string heuristic,
            bool b_excludeRoof)
        {
            switch (heuristic)
            {
                case "preserve_detail":
                    return b_downsampled_preserveDetail(export, rand, b_excludeRoof);
                case "preserve_structure":
                default:
                    return b_downsampled_preserveStructure(export, rand, b_excludeRoof);
            }
        }

        private bool b_downsampled_preserveDetail(FObjectExport export, Random rand,
            bool b_excludeRoof)
        {
            // 04: 0/0/90
            // 03: 0/0/95
            // 02: 90/15/90
            const int TerrainDownsampleRate = 0;
            const int GeneralDownsampleRate = 0;

            if (b_nonMesh(export))
                return true;

            if (b_excludeRoof && b_roofMesh(export))
                return true;

            if (b_specialProps(export))
                return false;

            /* True downsampling rate for high-frequency terrain:
             * high_freq_terrain_downsample_rate = 100 - high_freq_terrain_pass_rate
             *                                   = 100 - TerrainDownsampleRate - HighFrequencyDownsampleRate + TerrainDownsampleRate*HighFrequencyDownsampleRate
             *                                       + GeneralDownsampleRate*TerrainDownsampleRate + GeneralDownsampleRate*HighFrequencyRate
             *                                       - GeneralDownsampleRate*TerrainDownsampleRate*HighFrequencyDownsampleRate
             * high_freq_terrain_pass_rate = pass_high_freq_rate * pass_terrain_rate/100 * pass_general_rate/100
             * pass_high_freq_rate = 100 - TerrainDownsampleRate
             * pass_terrain_rate = 100 - HighFrequencyDownsampleRate
             * pass_general_rate = 100 - GeneralDownsampleRate
             */
            /* True downsampling rate for high-frequency non-terrain:
             * high_freq_non_terrain_downsample_rate = 100 - pass_high_freq_and_terrain_and_general_rate
             *                                       = HighFrequencyDownsampleRate + GeneralDownsampleRate - HighFrequencyDownsampleRate*GeneralDownsampleRate/100
             * pass_high_freq_and_terrain_and_general_rate = pass_high_freq_rate * pass_general_rate/100
             * pass_high_freq_rate = 100 - TerrainDownsampleRate
             * pass_general_rate = 100 - GeneralDownsampleRate
             */
            if (b_highFrequencyMesh(export, rand))
                return true;

            /* True downsampling rate for terrain:
             * terrain_downsample_rate = 100 - pass_terrain_and_general_rate
             *                         = TerrainDownsampleRate + GeneralDownsampleRate - TerrainDownsampleRate*GeneralDownsampleRate/100
             * pass_terrain_and_general_rate = pass_terrain_rate * pass_general_rate/100
             * pass_terrain_rate = 100 - TerrainDownsampleRate
             * pass_general_rate = 100 - GeneralDownsampleRate
             */
            if (b_terrainDownsampled(export, rand, TerrainDownsampleRate))
                return true;

            if (b_generalDownsampled(export, rand, GeneralDownsampleRate))
                return true;

            return false;
        }

        private bool b_downsampled_preserveStructure(FObjectExport export, Random rand,
            bool b_excludeRoof)
        {
            const int TerrainDownsampleRate = 50;
            const int GeneralDownsampleRate = 95;

            if (b_nonMesh(export))
                return true;

            if (b_excludeRoof && b_roofMesh(export))
                return true;

            // Filter out special props already preserved in lossy-structure heuristic
            if (b_specialProps(export))
                return true;

            // If a terrain mesh passes the structure downsampling, don't require it to also pass general downsampling
            if (!b_terrainDownsampled(export, rand, TerrainDownsampleRate))
                return false;

            if (b_generalDownsampled(export, rand, GeneralDownsampleRate))
                return true;

            return false;
        }

        private bool b_nonMesh(FObjectExport export)
        {
            if (export.ClassName.Contains("Blueprint")
                  || export.ClassName.Contains("Niagara")
                  || export.ClassName.Contains("Fog")
                  || export.ObjectName.ToString().Contains("Fog")
                  || export.ClassName.Contains("Decal")
                  || export.ObjectName.ToString().Contains("Decal")
                  || export.ObjectName.ToString().Contains("Light")
                  || export.ClassName.Contains("NavCollision")
                  || export.ObjectName.ToString().Contains("NavCollision")
                  || export.ClassName.Contains("SCS")
                  || export.ObjectName.ToString().Contains("SCS")
                  || export.ClassName.Contains("SkeletalMeshComponent")
                  || export.ObjectName.ToString().Contains("SkeletalMeshComponent"))
            { return true; }

            return false;
        }

        private bool b_roofMesh(FObjectExport export)
        {
            if (export.ObjectName.ToString().Contains("SM_Exhibition_Floor_01") // 04
                  || export.ObjectName.ToString().Contains("Roof"))
            { return true; }

            return false;
        }

        private bool b_specialProps(FObjectExport export)
        {
            if (export.ObjectName.ToString().Contains("Medical") // 04
                  && export.ObjectName.ToString().Contains("Lamp")
                  && export.ObjectName.ToString().Contains("lantern")
                  && export.ObjectName.ToString().Contains("Stool") // !02
                  && export.ObjectName.ToString().Contains("Book")
                  && export.ObjectName.ToString().Contains("Paper") // !02
                  && export.ObjectName.ToString().Contains("paper") // !02
                  && export.ObjectName.ToString().Contains("Dead")
                  && export.ObjectName.ToString().Contains("Corpse")
                  //&& export.ObjectName.ToString().Contains("Cage") // !03
                  && export.ObjectName.ToString().Contains("Remedy")
                  && export.ObjectName.ToString().Contains("Rack")
                  && export.ObjectName.ToString().Contains("Mortuary")
                  && export.ObjectName.ToString().Contains("Autopsy")
                  && export.ObjectName.ToString().Contains("Dissection")
                  && export.ObjectName.ToString().Contains("Troley")
                  && export.ObjectName.ToString().Contains("Device")
                  && export.ObjectName.ToString().Contains("Gate")
                  && export.ObjectName.ToString().Contains("Door")
                  && export.ObjectName.ToString().Contains("Underdark")
                  && export.ObjectName.ToString().Contains("Screen") // 03
                  && export.ObjectName.ToString().Contains("mattress")
                  && export.ObjectName.ToString().Contains("Pillow")
                  && export.ObjectName.ToString().Contains("bunk")
                  && export.ObjectName.ToString().Contains("Washstand"))
            { return true; }

            return false;
        }

        // If preserving other high-freq meshes: move untargeted meshes from here to terrain function
        private bool b_highFrequencyMesh(FObjectExport export, Random rand)
        {
            const int HighFrequencyDownscaleRate = 65;

            if ((export.ObjectName.ToString().Contains("Width") // 04
                  || export.ObjectName.ToString().Contains("BaseBottom")
                  || export.ObjectName.ToString().Contains("GroundRock") // 03
                  || export.ObjectName.ToString().Contains("Exterior_Block")
                  || export.ObjectName.ToString().Contains("Cathedral_Column")
                  || export.ObjectName.ToString().Contains("TrashPaper")
                  || export.ObjectName.ToString().Contains("Molding")
                  || export.ObjectName.ToString().Contains("Factory_Ladder_01")
                  || export.ObjectName.ToString().Contains("Factory_Pillar")
                  || export.ObjectName.ToString().Contains("Factory_Pipe")
                  || export.ObjectName.ToString().Contains("Molding")
                  || export.ObjectName.ToString().Contains("Interior_Pipe")
                  || export.ObjectName.ToString().Contains("IronProp")
                  || export.ObjectName.ToString().Contains("BossWall_01")
                  || export.ObjectName.ToString().Contains("Factory_HeightFrame")
                  || export.ObjectName.ToString().Contains("Rubble")
                  || export.ObjectName.ToString().Contains("Factory_Wall_01") // 03 - added to reduce overall high-freq downsampling rate
                  || export.ObjectName.ToString().Contains("Interior_Wall_20")
                  || export.ObjectName.ToString().Contains("Cathedral_BrickFloor_02")
                //|| export.ObjectName.ToString().Contains("Nature") // 02
                //|| export.ObjectName.ToString().Contains("Stool")
                //|| export.ObjectName.ToString().Contains("Rubble")
                //|| export.ObjectName.ToString().Contains("Ladder")
                //|| export.ObjectName.ToString().Contains("HeightFrame")
                ) && rand.Next(100) < HighFrequencyDownscaleRate)
            { return true; }

            return false;
        }

        private bool b_terrainDownsampled(FObjectExport export, Random rand, int downscaleRate)
        {
            if ((export.ObjectName.ToString().Contains("Wall") // 04
                   || export.ObjectName.ToString().Contains("Pillar")
                   || export.ObjectName.ToString().Contains("Floor")
                   || export.ObjectName.ToString().Contains("Pipe")
                   || export.ObjectName.ToString().Contains("Wire")
                 //export.ObjectName.ToString().Contains("Factory_Wall_01") // 03
                   //|| export.ObjectName.ToString().Contains("Interior_Wall_20")
                   //|| export.ObjectName.ToString().Contains("Cathedral_BrickFloor_02")
                ) && rand.Next(100) < downscaleRate)
            { return true; }

            return false;
        }
        
        private bool b_generalDownsampled(FObjectExport export, Random rand, int downscaleRate)
        {
            if ((export.ClassName.Contains("StaticMeshActor")
                   || export.ObjectName.ToString().Contains("StaticMeshActor")
                   || export.ClassName.Contains("StaticMeshComponent")
                   || export.ObjectName.ToString().Contains("StaticMeshComponent")
                ) && rand.Next(100) < downscaleRate)
            { return true; }

            return false;
        }

        public Package(FArchive uasset, FArchive? uexp, FArchive? ubulk = null, FArchive? uptnl = null,
            IFileProvider? provider = null, TypeMappings? mappings = null, bool useLazySerialization = true)
            : this(uasset, uexp, ubulk != null ? new Lazy<FArchive?>(() => ubulk) : null,
                uptnl != null ? new Lazy<FArchive?>(() => uptnl) : null, provider, mappings, useLazySerialization) { }

        public Package(string name, byte[] uasset, byte[]? uexp, byte[]? ubulk = null, byte[]? uptnl = null, IFileProvider? provider = null, bool useLazySerialization = true)
            : this(new FByteArchive($"{name}.uasset", uasset), uexp != null ? new FByteArchive($"{name}.uexp", uexp) : null,
                ubulk != null ? new FByteArchive($"{name}.ubulk", ubulk) : null,
                uptnl != null ? new FByteArchive($"{name}.uptnl", uptnl) : null, provider, null, useLazySerialization) { }

        public override UObject? GetExportOrNull(string name, StringComparison comparisonType = StringComparison.Ordinal)
        {
            try
            {
                return ExportMap
                    .FirstOrDefault(it => it.ObjectName.Text.Equals(name, comparisonType))?.ExportObject
                    .Value;
            }
            catch (Exception e)
            {
                Log.Debug(e, "Failed to get export object");
                return null;
            }
        }

        public override ResolvedObject? ResolvePackageIndex(FPackageIndex? index)
        {
            if (index == null || index.IsNull)
                return null;
            if (index.IsImport && -index.Index - 1 < ImportMap.Length)
                return ResolveImport(index);
            if (index.IsExport && index.Index - 1 < ExportMap.Length)
                return new ResolvedExportObject(index.Index - 1, this);
            return null;
        }

        private ResolvedObject? ResolveImport(FPackageIndex importIndex)
        {
            var import = ImportMap[-importIndex.Index - 1];
            var outerMostIndex = importIndex;
            FObjectImport outerMostImport;
            while (true)
            {
                outerMostImport = ImportMap[-outerMostIndex.Index - 1];
                if (outerMostImport.OuterIndex.IsNull)
                    break;
                outerMostIndex = outerMostImport.OuterIndex;
            }

            outerMostImport = ImportMap[-outerMostIndex.Index - 1];
            // We don't support loading script packages, so just return a fallback
            if (outerMostImport.ObjectName.Text.StartsWith("/Script/"))
            {
                return new ResolvedImportObject(import, this);
            }

            if (Provider == null)
                return null;
            Package? importPackage = null;
            if (Provider.TryLoadPackage(outerMostImport.ObjectName.Text, out var package))
                importPackage = package as Package;
            if (importPackage == null)
            {
#if DEBUG
                Log.Error("Missing native package ({0}) for import of {1} in {2}.", outerMostImport.ObjectName, import.ObjectName, Name);
#endif
                return new ResolvedImportObject(import, this);
            }

            string? outer = null;
            if (outerMostIndex != import.OuterIndex && import.OuterIndex.IsImport)
            {
                var outerImport = ImportMap[-import.OuterIndex.Index - 1];
                outer = ResolveImport(import.OuterIndex)?.GetPathName();
                if (outer == null)
                {
#if DEBUG
                    Log.Fatal("Missing outer for import of ({0}): {1} in {2} was not found, but the package exists.", Name, outerImport.ObjectName, importPackage.GetFullName());
#endif
                    return new ResolvedImportObject(import, this);
                }
            }

            for (var i = 0; i < importPackage.ExportMap.Length; i++)
            {
                var export = importPackage.ExportMap[i];
                if (export.ObjectName.Text != import.ObjectName.Text)
                    continue;
                var thisOuter = importPackage.ResolvePackageIndex(export.OuterIndex);
                if (thisOuter?.GetPathName() == outer)
                    return new ResolvedExportObject(i, importPackage);
            }

#if DEBUG
            Log.Fatal("Missing import of ({0}): {1} in {2} was not found, but the package exists.", Name, import.ObjectName, importPackage.GetFullName());
#endif
            return new ResolvedImportObject(import, this);
        }

        private class ResolvedExportObject : ResolvedObject
        {
            private readonly FObjectExport _export;

            public ResolvedExportObject(int exportIndex, Package package) : base(package, exportIndex)
            {
                _export = package.ExportMap[exportIndex];
            }

            public override FName Name => _export?.ObjectName ?? "None";
            public override ResolvedObject Outer => Package.ResolvePackageIndex(_export.OuterIndex) ?? new ResolvedLoadedObject((UObject) Package);
            public override ResolvedObject? Class => Package.ResolvePackageIndex(_export.ClassIndex);
            public override ResolvedObject? Super => Package.ResolvePackageIndex(_export.SuperIndex);
            public override Lazy<UObject> Object => _export.ExportObject;
        }

        /** Fallback if we cannot resolve the export in another package */
        private class ResolvedImportObject : ResolvedObject
        {
            private readonly FObjectImport _import;

            public ResolvedImportObject(FObjectImport import, Package package) : base(package)
            {
                _import = import;
            }

            public override FName Name => _import.ObjectName;
            public override ResolvedObject? Outer => Package.ResolvePackageIndex(_import.OuterIndex);
            public override ResolvedObject Class => new ResolvedLoadedObject(new UScriptClass(_import.ClassName.Text));
            public override Lazy<UObject>? Object => _import.ClassName.Text == "Class" ? new(() => new UScriptClass(Name.Text)) : null;
        }

        private class ExportLoader
        {
            private Package _package;
            private FObjectExport _export;
            private FAssetArchive _archive;
            private UObject _object;
            private List<LoadDependency>? _dependencies;
            private LoadPhase _phase = LoadPhase.Create;
            public Lazy<UObject> Lazy;

            public ExportLoader(Package package, FObjectExport export, FAssetArchive archive)
            {
                _package = package;
                _export = export;
                _archive = archive;
                Lazy = new(() =>
                {
                    Fire(LoadPhase.Serialize);
                    return _object;
                });
                export.ExportObject = Lazy;
            }

            private void EnsureDependencies()
            {
                if (_dependencies != null)
                {
                    return;
                }

                _dependencies = new();
                var runningIndex = _export.FirstExportDependency;
                if (runningIndex >= 0)
                {
                    for (var index = _export.SerializationBeforeSerializationDependencies; index > 0; index--)
                    {
                        var dep = _package.PreloadDependencies[runningIndex++];
                        // don't request IO for this export until these are serialized
                        _dependencies.Add(new(LoadPhase.Serialize, LoadPhase.Serialize, ResolveLoader(dep)));
                    }
                    for (var index = _export.CreateBeforeSerializationDependencies; index > 0; index--)
                    {
                        var dep = _package.PreloadDependencies[runningIndex++];
                        // don't request IO for this export until these are done
                        _dependencies.Add(new(LoadPhase.Serialize, LoadPhase.Create, ResolveLoader(dep)));
                    }
                    for (var index = _export.SerializationBeforeCreateDependencies; index > 0; index--)
                    {
                        var dep = _package.PreloadDependencies[runningIndex++];
                        // can't create this export until these things are serialized
                        _dependencies.Add(new(LoadPhase.Create, LoadPhase.Serialize, ResolveLoader(dep)));
                    }
                    for (var index = _export.CreateBeforeCreateDependencies; index > 0; index--)
                    {
                        var dep = _package.PreloadDependencies[runningIndex++];
                        // can't create this export until these things are created
                        _dependencies.Add(new(LoadPhase.Create, LoadPhase.Create, ResolveLoader(dep)));
                    }
                }
                else
                {
                    // We only need the outer to be created first
                    _dependencies.Add(new(LoadPhase.Create, LoadPhase.Create, ResolveLoader(_export.OuterIndex)));
                }
            }

            private ExportLoader? ResolveLoader(FPackageIndex index)
            {
                if (index.IsExport)
                {
                    return _package._exportLoaders[index.Index - 1];
                }
                return null;
            }

            private void Fire(LoadPhase untilPhase)
            {
                if (untilPhase >= LoadPhase.Create && _phase <= LoadPhase.Create)
                {
                    FireDependencies(LoadPhase.Create);
                    Create();
                }
                if (untilPhase >= LoadPhase.Serialize && _phase <= LoadPhase.Serialize)
                {
                    FireDependencies(LoadPhase.Serialize);
                    Serialize();
                }
            }

            private void FireDependencies(LoadPhase phase)
            {
                EnsureDependencies();
                foreach (var dependency in _dependencies)
                {
                    if (dependency.FromPhase == phase)
                    {
                        dependency.Target?.Fire(dependency.ToPhase);
                    }
                }
            }

            private void Create()
            {
                Trace.Assert(_phase == LoadPhase.Create);
                _phase = LoadPhase.Serialize;
                _object = ConstructObject(_package.ResolvePackageIndex(_export.ClassIndex)?.Object?.Value as UStruct);
                _object.Name = _export.ObjectName.Text;
                if (!_export.OuterIndex.IsNull)
                {
                    Trace.Assert(_export.OuterIndex.IsExport, "Outer imports are not yet supported");
                    _object.Outer = _package._exportLoaders[_export.OuterIndex.Index - 1]._object;
                }
                else
                {
                    _object.Outer = _package;
                }
                _object.Super = _package.ResolvePackageIndex(_export.SuperIndex) as ResolvedExportObject;
                _object.Template = _package.ResolvePackageIndex(_export.TemplateIndex) as ResolvedExportObject;
                _object.Flags |= (EObjectFlags) _export.ObjectFlags; // We give loaded objects the RF_WasLoaded flag in ConstructObject, so don't remove it again in here
            }

            private void Serialize()
            {
                Trace.Assert(_phase == LoadPhase.Serialize);
                _phase = LoadPhase.Complete;
                var Ar = (FAssetArchive) _archive.Clone();
                Ar.SeekAbsolute(_export.SerialOffset, SeekOrigin.Begin);
                DeserializeObject(_object, Ar, _export.SerialSize);
                // TODO right place ???
                _object.Flags |= EObjectFlags.RF_LoadCompleted;
                _object.PostLoad();
            }
        }

        private class LoadDependency
        {
            public LoadPhase FromPhase, ToPhase;
            public ExportLoader? Target;

            public LoadDependency(LoadPhase fromPhase, LoadPhase toPhase, ExportLoader? target)
            {
                FromPhase = fromPhase;
                ToPhase = toPhase;
                Target = target;
            }
        }

        private enum LoadPhase
        {
            Create, Serialize, Complete
        }
    }
}
