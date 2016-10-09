﻿using DereTore.Applications.StarlightDirector.Entities;

namespace DereTore.Applications.StarlightDirector.Conversion.Formats.Deleste {
    internal sealed class DelesteBasicNote {

        public DelesteBasicNote(DelesteBeatmapEntry entry) {
            Entry = entry;
        }

        public int IndexInMeasure { get; set; }

        public DelesteNoteType Type { get; set; }

        public NotePosition StartPosition { get; set; }

        public NotePosition FinishPosition { get; set; }

        public bool IsFlick => Type == DelesteNoteType.FlickLeft || Type == DelesteNoteType.FlickRight;

        public bool IsFlickLeft => Type == DelesteNoteType.FlickLeft;

        public bool IsFlickRight => Type == DelesteNoteType.FlickRight;

        public bool IsTap => Type == DelesteNoteType.Tap;

        public bool IsHoldStart => Type == DelesteNoteType.Hold;

        public int PositionInTrack => (int)FinishPosition - 1;

        public DelesteBeatmapEntry Entry { get; }

    }
}