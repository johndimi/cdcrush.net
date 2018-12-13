﻿using cdcrush.lib;
using cdcrush.lib.app;
using cdcrush.lib.task;

using System;
using System.IO;


namespace cdcrush.prog
{
	
	
// Every Crush Job runs with these input parameters
public struct CrushParams
{	
	// : Need to be set as job start parameters

	public string inputFile;	// The CUE file to compress
	public string outputDir;	// Output Directory.
	public string cdTitle;		// Custom CD TITLE
	public Tuple<string,int> audioQuality;	// Tuple<AudioCodecID, Quality Index>
	public string cover;			// Cover image for the CD, square
	public int archiveSettingsInd;	// If >-1 will create an archive using ArchiveMaster compression Index.
	public int expectedTracks;		// In order for the progress report to work. set num of CD tracks here.

	// 0:CRUSH, 1:Convert Only , 2:Convert and Archive
	public int mode;

	// : ----

	// Keep the CD infos of the CD, it is going to be read later
	public cd.CDInfos cd {get; internal set;}
	// Filesize of the final archive
	public int crushedSize {get; internal set;}
	// Temp dir for the current batch, it's autoset, is a subfolder of the master TEMP folder.
	internal string tempDir;
	// Final destination ARC file, autogenerated from CD TITLE
	internal string finalArcPath;
	// If true, then all the track files are stored in temp folder and safe to delete
	internal bool flag_sourceTracksOnTemp;
	
	// Archiver extension based on settings index (.zip)
	internal string archiver_ext;

	// Only valid in CONVERT operations
	internal string new_cue_path;
}// --


/// <summary>
/// A collection of tasks, that will CRUSH a cd,
/// Tasks will run in order, and some will run in parallel
/// </summary>
class JobCrush:CJob
{
	CrushParams p;
	
	// --
	public JobCrush(CrushParams par):base("Compress CD")
	{
		p = par;

		// Hack to fix progress
		hack_setExpectedProgTracks(p.expectedTracks + 3);

		// - Read CUE and some init
		// ---------------------------
		add(new CTask((t)=> {

			var cd = new cd.CDInfos();

			p.cd = cd;

			try{
				cd.cueLoad(p.inputFile);
			}catch(haxe.lang.HaxeException e) {
				t.fail(msg:e.Message); return;
			}

			// Meaning the tracks are going to be CUT in the temp folder, so they are safe to be removed
			p.flag_sourceTracksOnTemp = (!cd.MULTIFILE && cd.tracks.length > 1);

			// In case user named the CD, otherwise it's going to be the same
			if(!string.IsNullOrWhiteSpace(p.cdTitle))
			{
				cd.CD_TITLE = FileTools.sanitizeFilename(p.cdTitle);
			}

			// Real quality to string name
			cd.CD_AUDIO_QUALITY = AudioMaster.getCodecSettingsInfo(p.audioQuality);

			if(p.mode!=1) // Convert only does not require an archive
			{
				// Generate the final ARCHIVE path now that I have the CD TITLE
				p.finalArcPath = Path.Combine(p.outputDir, cd.CD_TITLE + p.archiver_ext);

				// Try to create a new archive in case it exists?
				while(File.Exists(p.finalArcPath))
				{
					LOG.log("{0} already exists, adding (_) until unique", p.finalArcPath);
					// S is entire path without (.ext)
					string S = p.finalArcPath.Substring(0,p.finalArcPath.Length - p.archiver_ext.Length);
					p.finalArcPath = S + "_" + p.archiver_ext;
				}

				LOG.log("- Destination Archive : {0}", p.finalArcPath);

			}

			if(p.mode==1)
			{
				// : ALWAYS Create a subfolder (when converting) to avoid overwriting the source files
				p.outputDir = CDCRUSH.checkCreateUniqueOutput(p.outputDir, p.cdTitle + CDCRUSH.RESTORED_FOLDER_SUFFIX);
				if(p.outputDir==null) {
					fail("Output Dir Error " + p.outputDir);
					return;
				}
			}

			jobData = p; // Some TASKS read jobData
			t.complete();

		},"-Reading", "Reading CUE data and preparing"));
		
		// - Cut tracks
		// ---------------------------
		add(new TaskCutTrackFiles());

		// - Encode tracks
		// ---------------------
		add(new CTask((t) =>
		{
			for(int i=0;i<p.cd.tracks.length;i++)
			{
				cd.CDTrack tr = p.cd.tracks[i] as cd.CDTrack;

				// Do not encode DATA TRACKS to ECM when converting.
				if(p.mode>0 && tr.isData) continue;
				
				addNextAsync(new TaskCompressTrack(tr));
			}

			t.complete();

		}, "-Preparing","Preparing to compress tracks"));



		// - Prepare Tracks on CONVERT modes
		// - Needed for the new .CUE to be created
		// - if CONVERT MODE, move all files to output
		if(p.mode>0)
		add(new CTask((t) => 
		{
			// DEV: So far :
			// track.trackFile is UNSET. cd.saveCue needs it to be set.
			// track.workingFile points to a valid file, some might be in TEMP folder and some in input folder (data tracks)

			int stepProgress = (int)Math.Round(100.0f/p.cd.tracks.length);

			// -- Move files to output folder
			for(int i=0;i<p.cd.tracks.length;i++)
			{
				cd.CDTrack track = p.cd.tracks[i] as cd.CDTrack;

				if(!p.cd.MULTIFILE) {
					// Fix the index times to start with 00:00:00
					track.rewriteIndexes_forMultiFile();
				}

				string ext = Path.GetExtension(track.workingFile);
				
				// This tells what the files should be named in the `.cue` file:
				track.trackFile = $"{p.cd.CD_TITLE} (track {track.trackNo}){ext}";

				// Data track was not cut or encoded.
				// It's in the input folder, don't move it
				if(track.isData && p.cd.MULTIFILE)
				{
					if(p.mode==1)
					{
						FileTools.tryCopy(track.workingFile, Path.Combine(p.outputDir, track.trackFile));
						track.workingFile = Path.Combine(p.outputDir, track.trackFile);
					}
					else
					{
						// I need to copy all files to TEMP, so that they can be renamed and archived
						FileTools.tryCopy(track.workingFile, Path.Combine(p.tempDir, track.trackFile));
						track.workingFile = Path.Combine(p.tempDir, track.trackFile);
					}
				}
				else // encoded file that is on TEMP or OUTPUT
				{
					if(p.mode==1)
					{
						// TaskCompress already put the audio files on the output folder
						// But it's no big deal calling it again
						// This is for the data tracks that are on the temp folder
						FileTools.tryMove(track.workingFile, Path.Combine(p.outputDir, track.trackFile));
						track.workingFile = Path.Combine(p.outputDir, track.trackFile);
					}else
					{
						// Track that has been encoded and is on TEMP
						// It is currently named as "track_xx.xx" so rename it 
						FileTools.tryMove(track.workingFile, Path.Combine(p.tempDir, track.trackFile));
						track.workingFile = Path.Combine(p.tempDir, track.trackFile);
					}
				}

				t.PROGRESS += stepProgress;
			} // -- end processing tracks


			if(p.mode==1)
			{
				p.new_cue_path = Path.Combine(p.outputDir,p.cd.CD_TITLE + ".cue");
			}else
			{
				p.new_cue_path = Path.Combine(p.tempDir,p.cd.CD_TITLE + ".cue");
			}

			//. Create the new CUE file
			try{
				p.cd.cueSave(
					p.new_cue_path ,
					new haxe.root.Array<object>( new [] {
						"CDCRUSH (dotNet) version : " + CDCRUSH.PROGRAM_VERSION,
						CDCRUSH.LINK_SOURCE
				}));

			}catch(haxe.lang.HaxeException e){
				t.fail(msg:e.Message); return;
			}

			t.complete();

		},"Converting"));


		// - Create an Archive
		// Add all tracks to the final archive
		// ---------------------
		if(p.mode!=1)	
		add(new CTask((t) => 
		{
			// -- Get list of files to compress
			// . Tracks
			System.Collections.ArrayList files = new System.Collections.ArrayList();
			for(var i=0;i<p.cd.tracks.length;i++) {
				files.Add((p.cd.tracks[i] as cd.CDTrack).workingFile); // Working file is valid, was set earlier
			}

			if(p.mode==0) // Only on CDCRUSH add cover and json data
			{
				// . Settings
				string path_settings = Path.Combine(p.tempDir, CDCRUSH.CDCRUSH_SETTINGS);
				try{
					p.cd.jsonSave(path_settings);
					files.Add(path_settings);
				}catch(haxe.lang.HaxeException e){
					t.fail(msg:e.Message); return;
				}
	
				// . Cover Image
				string path_cover;
				if(p.cover!=null) {
					path_cover = Path.Combine(p.tempDir,CDCRUSH.CDCRUSH_COVER);
					File.Copy(p.cover,path_cover);
					files.Add(path_cover);
				}else {
					path_cover = null;
				}
			}else
			{
				// It must be CONVERT + ARCHIVE
				files.Add(p.new_cue_path);
			}

			// -. Compress whatever files are on
			var arc = ArchiveMaster.getArchiver(p.finalArcPath);
			
			string arcStr = ArchiveMaster.getCompressionSettings(p.archiveSettingsInd).Item2;

			arc.compress((string[])files.ToArray(typeof(string)), jobData.finalArcPath, -1, arcStr);
			arc.onProgress = (pr) => t.PROGRESS = pr;
			arc.onComplete = (s) => {
				if(s){
					// NOTE: This var is autowritten whenever a compress operation is complete
					p.crushedSize = (int)arc.COMPRESSED_SIZE;
					// IMPORTANT to write to jobdata, because it is not a pointer and this needs to be read externally
					jobData = p;
					t.complete();
				}else{
					fail(arc.ERROR);
				}
			};

			t.killExtra = () => arc.kill();

		}, "Compressing", "Compressing everything into an archive"));

	
		// -- COMPLETE --

		add(new CTask((t)=>
		{
			LOG.log("== Detailed CD INFOS ==");
			LOG.log(p.cd.getDetailedInfo());
			t.complete();
		},"-complete"));

		

	}// -----------------------------------------


	// Check for input parameters
	// :: ------------------------
	private void check_parameters()
	{
		p.archiver_ext = '.' + ArchiveMaster.getCompressionSettings(p.archiveSettingsInd).Item1;
		p.tempDir = CDCRUSH.getSubTempDir();

		if(!FileTools.createDirectory(p.tempDir)) {
			fail(msg: "Can't create TEMP dir");
			return;
		}

		if(!CDCRUSH.check_file_(p.inputFile,".cue")) {
			fail(msg:CDCRUSH.ERROR);
			return;
		}

		if(string.IsNullOrEmpty(p.outputDir)) {
			p.outputDir = Path.GetDirectoryName(p.inputFile);
		}

		if(!FileTools.createDirectory(p.outputDir)) {
			fail(msg: "Can't create Output Dir " + p.outputDir);
			return;
		}

	}


	// -
	public override void start()
	{
		check_parameters();

		LOG.line();
		LOG.log("= COMPRESSING A CD with the following parameters :");
		LOG.log("- Input : {0}", p.inputFile);
		LOG.log("- Output Dir : {0}", p.outputDir);
		LOG.log("- Temp Dir : {0}", p.tempDir);
		LOG.log("- CD Title  : {0}", p.cdTitle);
		LOG.log("- Audio Quality : {0}",AudioMaster.getCodecSettingsInfo(p.audioQuality));
		LOG.log("- Compression : {0}", ArchiveMaster.compressionStrings[p.archiveSettingsInd] );
		LOG.log("- Cover Image : {0}",p.cover);
		base.start();
	}// -----------------------------------------

	/// <summary>
	/// Called on FAIL / COMPLETE / PROGRAM EXIT
	/// Clean up temporary files
	/// </summary>
	protected override void kill()
	{
		base.kill();

		if(CDCRUSH.FLAG_KEEP_TEMP) return;

		// - Cleanup
		if (p.tempDir != p.outputDir)  // NOTE: This is always a subdir of the master temp dir
		{ 
			try {
				Directory.Delete(p.tempDir, true);
			} catch(IOException) {
				// do nothing
			}
		}// --	
	}// -----------------------------------------

}// --
}// --
