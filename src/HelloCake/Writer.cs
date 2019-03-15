using System;

namespace HelloCake
{
    public class Writer
    {
        /// <summary>
        /// Retrive greeting message.
        /// </summary>
        /// <param name="name">The name to be greeted.</param>
        /// <returns>Greeting message</returns>
        public String Greeting(String name) => String.IsNullOrWhiteSpace(name) ? $"Yo Bro!" : $"Hello {name}!";
    }
}
