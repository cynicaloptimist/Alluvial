namespace Alluvial.Tests
{
    public class FileStreamCursor : ICursor
    {
        private long position;

        public FileStreamCursor(long initialPosition = 0)
        {
            position = initialPosition;
        }

        public dynamic Position
        {
            get { return position; }
        }

        public bool Ascending
        {
            get { return true; }
        }

        public void AdvanceTo(dynamic position)
        {
            this.position = (long) position;
        }

        public bool HasReached(dynamic point)
        {
            return point >= position;
        }
    }
}