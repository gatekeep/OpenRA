#region Copyright & License Information
/*
 * Copyright 2007-2019 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.FileSystem;
using OpenRA.Mods.Common.UpdateRules;

namespace OpenRA.Mods.Common.UtilityCommands
{
	class ConvertMapCommand : IUtilityCommand
	{
		string IUtilityCommand.Name { get { return "--convert-map"; } }

		bool IUtilityCommand.ValidateArguments(string[] args)
		{
			return args.Length >= 3;
		}

		[Desc("MAP DESTMOD DESTMAP", "Performs a \"best try\" map conversion from one mod to another. This will only work if the mods have compatible tilesets!")]
		void IUtilityCommand.Run(Utility utility, string[] args)
		{
			// HACK: The engine code assumes that Game.modData is set.
			var modData = Game.ModData = utility.ModData;

			var envModSearchPaths = Environment.GetEnvironmentVariable("MOD_SEARCH_PATHS");
			var modSearchPaths = !string.IsNullOrWhiteSpace(envModSearchPaths) ?
				FieldLoader.GetValue<string[]>("MOD_SEARCH_PATHS", envModSearchPaths) :
				new[] { Path.Combine(".", "mods") };

			var modId = args[2];
			var explicitModPaths = new string[0];
			if (File.Exists(modId) || Directory.Exists(modId))
			{
				explicitModPaths = new[] { modId };
				modId = Path.GetFileNameWithoutExtension(modId);
			}

			var mods = new InstalledMods(modSearchPaths, explicitModPaths);
			if (!mods.Keys.Contains(modId))
			{
				Console.WriteLine("Must specify a destination mod!");
				Console.WriteLine("The available mods are: " + string.Join(", ", mods.Keys));
				Console.WriteLine();
				return;
			}

			var destModData = new ModData(mods[modId], mods);

			// load source mod tile sets
			var modTileSets = new List<TileSet>();
			foreach (var t in modData.Manifest.TileSets)
				modTileSets.Add(new TileSet(modData.DefaultFileSystem, t));

			// load destination mod tile sets
			var destModTileSets = new List<TileSet>();
			foreach (var t in destModData.Manifest.TileSets)
				destModTileSets.Add(new TileSet(destModData.DefaultFileSystem, t));

			// HACK: We know that maps can only be oramap or folders, which are ReadWrite
			var package = new Folder(".").OpenPackage(args[1], modData.ModFiles) as IReadWritePackage;
			if (package == null)
				throw new FileNotFoundException(args[1]);

			// load source map
			var map = new Map(modData, package);
			if (map != null)
			{
				// generate destination map
				// Require at least a 2x2 playable area so that the
				// ground is visible through the edge shroud
				int width = Math.Max(2, map.MapSize.X);
				int height = Math.Max(2, map.MapSize.Y);

				var maxTerrainHeight = map.Grid.MaximumTerrainHeight;

				var srcTileset = modTileSets.Find((x) => x.Id == map.Tileset);
				var dstTileset = destModTileSets.Find((x) => x.Id == map.Tileset);

				if (srcTileset == null)
				{
					Console.WriteLine(string.Format("Could not load the source mod terrain tileset. {0} must exist!", map.Tileset));
					return;
				}

				if (dstTileset == null)
				{
					Console.WriteLine(string.Format("Could not load the destination mod terrain tileset. {0} must exist!", map.Tileset));
					return;
				}

				// create tileset template map; mapping the source templates to the destination templates (if possible)
				var templateMap = new Dictionary<ushort, ushort>();
				foreach (var template in srcTileset.Templates)
				{
					ushort sourceId = template.Key;
					TerrainTemplateInfo tti = template.Value;
					if (tti == null)
					{
						Console.WriteLine(string.Format("No source {0} template info?", sourceId));
						continue;
					}
					else
					{
						if (tti.TilesCount > 0)
						{
							if (dstTileset.Templates.ContainsKey(sourceId))
							{
								TerrainTemplateInfo destTTI = dstTileset.Templates[sourceId];
								if (tti.Images[0].ToLower() == destTTI.Images[0].ToLower() && tti.Size == destTTI.Size &&
									tti.Categories[0].ToLower() == destTTI.Categories[0].ToLower())
								{
									ushort destId = destTTI.Id;
									Console.WriteLine(string.Format("Mapping source {0} to destination {1} for {2}", sourceId, destId, tti.Images[0]));
									templateMap.Add(sourceId, destId);
									continue;
								}
							}

							// if we got here we didn't map the tile above intelligently -- so now we'll look through all the destinations
							foreach (var destTemplate in dstTileset.Templates)
							{
								ushort destId = destTemplate.Key;
								TerrainTemplateInfo destTTI = destTemplate.Value;

								byte terrainType = 0;
								for (int i = 0; i < destTTI.TilesCount; i++)
									if (destTTI[i] != null)
									{
										terrainType = destTTI[i].TerrainType;
										break;
									}

								if (destTTI == null)
								{
									Console.WriteLine(string.Format("No destination {0} template info?", destId));
									continue;
								}
								else
								{
									if (tti.Images[0].ToLower() == destTTI.Images[0].ToLower() && tti.Size == destTTI.Size &&
										tti.Categories[0].ToLower() == destTTI.Categories[0].ToLower())
									{
										Console.WriteLine(string.Format("Mapping source {0} to destination {1} for {2}", sourceId, destId, tti.Images[0]));
										templateMap.Add(sourceId, destId);
										break;
									}
								}
							}
						}
					}
				}

				if (templateMap.Count > 0)
				{
					var destMap = new Map(destModData, dstTileset, width + 2, height + maxTerrainHeight + 2);

					var tl = new PPos(1, 1 + maxTerrainHeight);
					var br = new PPos(width, height + maxTerrainHeight);
					destMap.SetBounds(tl, br);

					destMap.PlayerDefinitions = new MapPlayers(map.Rules, 0).ToMiniYaml();
					destMap.FixOpenAreas();

					destMap.Author = map.Author;
					destMap.Title = map.Title;

					var srcMapTiles = map.Tiles;
					var srcMapHeight = map.Height;
					var srcMapResources = map.Resources;

					var dstMapTiles = destMap.Tiles;
					var dstMapHeight = destMap.Height;
					var dstMapResources = destMap.Resources;

					// process non-edge cells
					var tiles = new Dictionary<CPos, Tuple<TerrainTile, ResourceTile, byte>>();
					foreach (var cell in map.AllCells)
						tiles.Add(cell, Tuple.Create(srcMapTiles[cell], srcMapResources[cell], srcMapHeight[cell]));

					foreach (var kv in tiles)
					{
						if (templateMap.ContainsKey(kv.Value.Item1.Type))
						{
							var mappedTileType = templateMap[kv.Value.Item1.Type];
							dstMapTiles[kv.Key] = new TerrainTile(mappedTileType, kv.Value.Item1.Index);
						}
						else
						{
							dstMapTiles[kv.Key] = kv.Value.Item1; // this will result in corruption! but the mapper should be fixing up the map anyway
						}

						dstMapResources[kv.Key] = kv.Value.Item2;
						dstMapHeight[kv.Key] = kv.Value.Item3;
					}

					destMap.ActorDefinitions = map.ActorDefinitions;
					destMap.PlayerDefinitions = map.PlayerDefinitions;
					destMap.RequiresMod = destModData.Manifest.Id;

					var combinedPath = Platform.ResolvePath(Path.Combine(Environment.CurrentDirectory, args[3]));
					try
					{
						var destPackage = destMap.Package as IReadWritePackage;
						package = ZipFileLoader.Create(combinedPath);

						destMap.Save(package);

						Console.WriteLine("Saved converted map at {0}", combinedPath);
						Console.WriteLine("NOTE: The conversion process is \"best try\"! This means it may leave invalid actor definitions and/or player definitions! Final manual cleanup of the map will be required!");
					}
					catch (Exception e)
					{
						Console.WriteLine("Could not save converted map at {0}. {1}", combinedPath, e.Message);
						Log.Write("debug", "Failed to save map at {0}: {1}", combinedPath, e.Message);
						Log.Write("debug", "{0}", e.StackTrace);
					}
				}
			}
		}
	}
}
