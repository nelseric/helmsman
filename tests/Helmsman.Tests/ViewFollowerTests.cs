using Helmsman.Core;
using Xunit;

namespace Helmsman.Tests
{
    public class ViewFollowerTests
    {
        [Fact]
        public void FirstFrame_DoesNotTurn()
        {
            Assert.Equal(0f, new ViewFollower().Step(123f));
        }

        [Fact]
        public void TurnsByTheShipsHeadingChange()
        {
            var view = new ViewFollower();
            view.Step(10f);
            Assert.Equal(5f, view.Step(15f), 3);
            Assert.Equal(-7f, view.Step(8f), 3);
        }

        [Theory]
        [InlineData(358f, 3f, 5f)]
        [InlineData(3f, 358f, -5f)]
        [InlineData(-179f, 179f, -2f)]
        public void WrapsAroundNorth(float from, float to, float expected)
        {
            var view = new ViewFollower();
            view.Step(from);
            Assert.Equal(expected, view.Step(to), 3);
        }

        [Fact]
        public void Reset_ForgetsTheLastHeading()
        {
            var view = new ViewFollower();
            view.Step(10f);
            view.Reset();
            Assert.Equal(0f, view.Step(90f));
        }
    }
}
