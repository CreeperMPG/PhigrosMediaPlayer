using System;

namespace PhigrosMediaPlayer.Models
{
    public class Track
    {
        public string Title { get; set; }
        public string Path { get; set; }

        public override string ToString()
        {
            return Title ?? System.IO.Path.GetFileName(Path);
        }
    }
}