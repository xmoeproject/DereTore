﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using StarlightDirector.Entities;

namespace StarlightDirector.Exchange {
    public static partial class ProjectIO {

        public static void Save(Project project) {
            Save(project, project.SaveFileName);
        }

        public static void Save(Project project, string fileName) {
            var fileInfo = new FileInfo(fileName);
            var newDatabase = !fileInfo.Exists;
            Save(project, fileName, newDatabase, false);
        }

        public static void SaveAsBackup(Project project, string fileName) {
            Save(project, fileName, true, true);
        }

        public static Project Load(string fileName) {
            var version = CheckProjectFileVersion(fileName);
            return Load(fileName, version);
        }

        internal static Project Load(string fileName, int versionOverride) {
            Project project = null;
            switch (versionOverride) {
                case ProjectVersion.Unknown:
                    throw new ArgumentOutOfRangeException(nameof(versionOverride));
                case ProjectVersion.V0_1:
                    project = LoadFromV01(fileName);
                    break;
                case ProjectVersion.V0_2:
                    project = LoadFromV02(fileName);
                    break;
                case ProjectVersion.V0_3:
                    // Note (2017-Feb-28):
                    // Slide note is added to CGSS a month before. SLDPROJ format v0.3.1 uses the "prev_flick" and "next_flick" fields to
                    // store slide notes relation info, because slide and flick notes have similar behaviors and they can be distinguished
                    // by the "type" field. So v0.3.1 is a super set of v0.3. The exceptions are handled in ReadScore() (see below).
                    break;
            }

            if (project == null)
                project = LoadFromV03x(fileName);

            // Update bar timings, sort notes
            foreach (var difficulty in Difficulties) {
                var score = project.GetScore(difficulty);
                foreach (var bar in score.Bars) {
                    bar.UpdateTimings();
                    bar.Notes.Sort(Note.TimingThenPositionComparison);
                }

                score.Notes.Sort(Note.TimingThenPositionComparison);
            }

            return project;
        }

        private static void Save(Project project, string fileName, bool createNewDatabase, bool isBackup) {
            var fileInfo = new FileInfo(fileName);
            fileName = fileInfo.FullName;
            if (createNewDatabase) {
                SQLiteConnection.CreateFile(fileName);
            } else {
                File.Delete(fileName);
            }
            var builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = fileName;
            using (var connection = new SQLiteConnection(builder.ToString())) {
                connection.Open();
                SQLiteCommand setValue = null, insertNote = null, insertNoteID = null, insertBarParams = null, insertSpecialNote = null;

                using (var transaction = connection.BeginTransaction()) {
                    // Table structure
                    SQLiteHelper.CreateKeyValueTable(transaction, Names.Table_Main);
                    SQLiteHelper.CreateScoresTable(transaction);
                    SQLiteHelper.CreateKeyValueTable(transaction, Names.Table_ScoreSettings);
                    SQLiteHelper.CreateKeyValueTable(transaction, Names.Table_Metadata);
                    SQLiteHelper.CreateBarParamsTable(transaction);
                    SQLiteHelper.CreateSpecialNotesTable(transaction);

                    // Main
                    SQLiteHelper.InsertValue(transaction, Names.Table_Main, Names.Field_MusicFileName, project.MusicFileName ?? string.Empty, ref setValue);
                    SQLiteHelper.InsertValue(transaction, Names.Table_Main, Names.Field_Version, project.Version, ref setValue);

                    // Notes
                    SQLiteHelper.InsertNoteID(transaction, EntityID.Invalid, ref insertNoteID);
                    foreach (var difficulty in Difficulties) {
                        var score = project.GetScore(difficulty);
                        foreach (var note in score.Notes) {
                            if (note.IsGamingNote) {
                                SQLiteHelper.InsertNoteID(transaction, note.ID, ref insertNoteID);
                            }
                        }
                    }
                    foreach (var difficulty in Difficulties) {
                        var score = project.GetScore(difficulty);
                        foreach (var note in score.Notes) {
                            if (note.IsGamingNote) {
                                SQLiteHelper.InsertNote(transaction, note, ref insertNote);
                            }
                        }
                    }

                    // Score settings
                    var settings = project.Settings;
                    SQLiteHelper.InsertValue(transaction, Names.Table_ScoreSettings, Names.Field_GlobalBpm, settings.GlobalBpm.ToString(CultureInfo.InvariantCulture), ref setValue);
                    SQLiteHelper.InsertValue(transaction, Names.Table_ScoreSettings, Names.Field_StartTimeOffset, settings.StartTimeOffset.ToString(CultureInfo.InvariantCulture), ref setValue);
                    SQLiteHelper.InsertValue(transaction, Names.Table_ScoreSettings, Names.Field_GlobalGridPerSignature, settings.GlobalGridPerSignature.ToString(), ref setValue);
                    SQLiteHelper.InsertValue(transaction, Names.Table_ScoreSettings, Names.Field_GlobalSignature, settings.GlobalSignature.ToString(), ref setValue);

                    // Bar params && Special notes
                    foreach (var difficulty in Difficulties) {
                        var score = project.GetScore(difficulty);
                        foreach (var bar in score.Bars) {
                            if (bar.Params != null) {
                                SQLiteHelper.InsertBarParams(transaction, bar, ref insertBarParams);
                            }
                        }
                        foreach (var note in score.Notes.Where(note => !note.IsGamingNote)) {
                            SQLiteHelper.InsertNoteID(transaction, note.ID, ref insertNoteID);
                            SQLiteHelper.InsertSpecialNote(transaction, note, ref insertSpecialNote);
                        }
                    }

                    // Metadata (none for now)

                    // Commit!
                    transaction.Commit();
                }

                // Cleanup
                insertNoteID?.Dispose();
                insertNote?.Dispose();
                setValue?.Dispose();
                connection.Close();
            }
            if (!isBackup) {
                project.SaveFileName = fileName;
                project.IsChanged = false;
            }
        }

        private static Project LoadFromV03x(string fileName) {
            var fileInfo = new FileInfo(fileName);
            if (!fileInfo.Exists) {
                throw new FileNotFoundException(string.Empty, fileName);
            }
            fileName = fileInfo.FullName;
            var project = new Project {
                IsChanged = false,
                SaveFileName = fileName
            };
            var builder = new SQLiteConnectionStringBuilder();
            builder.DataSource = fileName;
            using (var connection = new SQLiteConnection(builder.ToString())) {
                connection.Open();
                SQLiteCommand getValues = null;

                // Main
                var mainValues = SQLiteHelper.GetValues(connection, Names.Table_Main, ref getValues);
                project.MusicFileName = mainValues[Names.Field_MusicFileName];
                var projectVersionString = mainValues[Names.Field_Version];
                float fProjectVersion;
                float.TryParse(projectVersionString, out fProjectVersion);
                if (fProjectVersion <= 0) {
                    Debug.Print("WARNING: incorrect project version: {0}", projectVersionString);
                    fProjectVersion = ProjectVersion.Current;
                }
                if (fProjectVersion < 1) {
                    fProjectVersion *= 1000;
                }
                var projectVersion = (int)fProjectVersion;
                // Keep project.Version property having the newest project version.

                // Scores
                foreach (var difficulty in Difficulties) {
                    var score = new Score(project, difficulty);
                    ReadScore(connection, score, projectVersion);
                    score.ResolveReferences(project);
                    score.FixSyncNotes();
                    score.Difficulty = difficulty;
                    project.Scores.Add(difficulty, score);
                }

                // Score settings
                var scoreSettingsValues = SQLiteHelper.GetValues(connection, Names.Table_ScoreSettings, ref getValues);
                var settings = project.Settings;
                settings.GlobalBpm = double.Parse(scoreSettingsValues[Names.Field_GlobalBpm]);
                settings.StartTimeOffset = double.Parse(scoreSettingsValues[Names.Field_StartTimeOffset]);
                settings.GlobalGridPerSignature = int.Parse(scoreSettingsValues[Names.Field_GlobalGridPerSignature]);
                settings.GlobalSignature = int.Parse(scoreSettingsValues[Names.Field_GlobalSignature]);

                // Bar params
                if (SQLiteHelper.DoesTableExist(connection, Names.Table_BarParams)) {
                    foreach (var difficulty in Difficulties) {
                        var score = project.GetScore(difficulty);
                        ReadBarParams(connection, score);
                    }
                }

                // Special notes
                if (SQLiteHelper.DoesTableExist(connection, Names.Table_SpecialNotes)) {
                    foreach (var difficulty in Difficulties) {
                        var score = project.GetScore(difficulty);
                        ReadSpecialNotes(connection, score);
                    }
                }

                // Metadata (none for now)

                // Cleanup
                getValues.Dispose();
                connection.Close();
            }

            GridLineFixup(project);
            return project;
        }

        private static void GridLineFixup(Project project) {
            // Signature fix-up
            var newGrids = ScoreSettings.DefaultGlobalGridPerSignature * ScoreSettings.DefaultGlobalSignature;
            var oldGrids = project.Settings.GlobalGridPerSignature * project.Settings.GlobalSignature;
            if (newGrids == oldGrids) {
                return;
            }
            project.Settings.GlobalGridPerSignature = ScoreSettings.DefaultGlobalGridPerSignature;
            project.Settings.GlobalSignature = ScoreSettings.DefaultGlobalSignature;
            if (newGrids % oldGrids == 0) {
                // Expanding (e.g. 48 -> 96)
                var k = newGrids / oldGrids;
                foreach (var difficulty in Difficulties) {
                    if (!project.Scores.ContainsKey(difficulty)) {
                        continue;
                    }
                    var score = project.GetScore(difficulty);
                    foreach (var note in score.Notes) {
                        note.SetIndexInGridWithoutSorting(note.IndexInGrid * k);
                    }
                }
            } else if (oldGrids % newGrids == 0) {
                // Shrinking (e.g. 384 -> 96)
                var k = oldGrids / newGrids;
                var incompatibleNotes = new List<Note>();
                foreach (var difficulty in Difficulties) {
                    if (!project.Scores.ContainsKey(difficulty)) {
                        continue;
                    }
                    var score = project.GetScore(difficulty);
                    foreach (var note in score.Notes) {
                        if (note.IndexInGrid % k != 0) {
                            incompatibleNotes.Add(note);
                        } else {
                            note.SetIndexInGridWithoutSorting(note.IndexInGrid / k);
                        }
                    }
                }
                if (incompatibleNotes.Count > 0) {
                    Debug.Print("Notes on incompatible grid lines are found. Removing.");
                    foreach (var note in incompatibleNotes) {
                        note.Bar.RemoveNote(note);
                    }
                }
            }
        }

        private static void ReadScore(SQLiteConnection connection, Score score, int projectVersion) {
            using (var table = new DataTable()) {
                SQLiteHelper.ReadNotesTable(connection, score.Difficulty, table);
                // v0.3.1: "note_type"
                // Only flick existed when there is a flick-alike relation. Now, both flick and slide are possible.
                var hasNoteTypeColumn = projectVersion >= ProjectVersion.V0_3_1;
                foreach (DataRow row in table.Rows) {
                    var id = (int)(long)row[Names.Column_ID];
                    var barIndex = (int)(long)row[Names.Column_BarIndex];
                    var grid = (int)(long)row[Names.Column_IndexInGrid];
                    var start = (NotePosition)(long)row[Names.Column_StartPosition];
                    var finish = (NotePosition)(long)row[Names.Column_FinishPosition];
                    var flick = (NoteFlickType)(long)row[Names.Column_FlickType];
                    var prevFlick = (int)(long)row[Names.Column_PrevFlickNoteID];
                    var nextFlick = (int)(long)row[Names.Column_NextFlickNoteID];
                    var hold = (int)(long)row[Names.Column_HoldTargetID];
                    var noteType = hasNoteTypeColumn ? (NoteType)(long)row[Names.Column_NoteType] : NoteType.TapOrFlick;

                    EnsureBarIndex(score, barIndex);
                    var bar = score.Bars[barIndex];
                    var note = bar.AddNoteWithoutUpdatingGlobalNotes(id);
                    if (note != null) {
                        note.IndexInGrid = grid;
                        note.StartPosition = start;
                        note.FinishPosition = finish;
                        note.Type = noteType;
                        note.FlickType = flick;
                        note.PrevFlickOrSlideNoteID = prevFlick;
                        note.NextFlickOrSlideNoteID = nextFlick;
                        note.HoldTargetID = hold;
                    } else {
                        Debug.Print("Note with ID '{0}' already exists.", id);
                    }
                }
            }
        }

        private static void ReadBarParams(SQLiteConnection connection, Score score) {
            using (var table = new DataTable()) {
                SQLiteHelper.ReadBarParamsTable(connection, score.Difficulty, table);
                foreach (DataRow row in table.Rows) {
                    var index = (int)(long)row[Names.Column_BarIndex];
                    var grid = (int?)(long?)row[Names.Column_GridPerSignature];
                    var signature = (int?)(long?)row[Names.Column_Signature];
                    if (index < score.Bars.Count) {
                        score.Bars[index].Params = new BarParams {
                            UserDefinedGridPerSignature = grid,
                            UserDefinedSignature = signature
                        };
                    }
                }
            }
        }

        private static void ReadSpecialNotes(SQLiteConnection connection, Score score) {
            using (var table = new DataTable()) {
                SQLiteHelper.ReadSpecialNotesTable(connection, score.Difficulty, table);
                foreach (DataRow row in table.Rows) {
                    var id = (int)(long)row[Names.Column_ID];
                    var barIndex = (int)(long)row[Names.Column_BarIndex];
                    var grid = (int)(long)row[Names.Column_IndexInGrid];
                    var type = (int)(long)row[Names.Column_NoteType];
                    var paramsString = (string)row[Names.Column_ParamValues];
                    if (barIndex < score.Bars.Count) {
                        var bar = score.Bars[barIndex];
                        // Special notes are not added during the ReadScores() process, so we call AddNote() rather than AddNoteWithoutUpdatingGlobalNotes().
                        var note = bar.Notes.FirstOrDefault(n => n.Type == (NoteType)type && n.IndexInGrid == grid);
                        if (note == null) {
                            note = bar.AddNote(id);
                            note.SetSpecialType((NoteType)type);
                            note.IndexInGrid = grid;
                            note.ExtraParams = NoteExtraParams.FromDataString(paramsString, note);
                        } else {
                            note.ExtraParams.UpdateByDataString(paramsString);
                        }
                    }
                }
            }
        }

        private static void EnsureBarIndex(Score score, int index) {
            if (score.Bars.Count > index) {
                return;
            }
            for (var i = score.Bars.Count; i <= index; ++i) {
                var bar = new Bar(score, i);
                score.Bars.Add(bar);
            }
        }

        private static readonly Difficulty[] Difficulties = { Difficulty.Debut, Difficulty.Regular, Difficulty.Pro, Difficulty.Master, Difficulty.MasterPlus };

    }
}
