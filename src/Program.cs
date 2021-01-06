namespace RefreshCSTest
{
    class Program
    {
        static void Main(string[] args)
        {
            using (TestGame game = new TestGame())
            {
                var init = game.Initialize(1280, 720);

                if (init)
                {
                    game.Run();
                }
                else
                {
                    System.Console.WriteLine("uh oh!");
                }
            }
        }
    }
}
