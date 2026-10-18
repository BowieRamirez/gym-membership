using GymMembership.Core.Services;
using Xunit;

/// <summary>When the app is newer than the database (a migration was not pushed), the message says so instead of "Something went wrong".</summary>
public class ServerOutOfDateTests
{
    const string Expected = "This part of the app needs a server update that hasn't been installed yet. Tell the gym owner.";

    [Theory]
    [InlineData("{\"code\":\"PGRST202\",\"message\":\"Could not find the function public.chat_threads without parameters in the schema cache\"}")]
    [InlineData("Could not find the function public.set_my_avatar(p_path) in the schema cache")]
    [InlineData("Could not find the 'attachment_path' column of 'messages' in the schema cache")]
    [InlineData("Could not find the table 'public.support_threads' in the schema cache")]
    public void A_missing_function_column_or_table_is_explained(string raw)
    {
        Assert.Equal(Expected, ErrorText.For(ErrorMapper.Map(new Exception(raw)), raw));
    }

    [Theory]
    [InlineData("something odd")]
    [InlineData("permission denied for function hire_coach")]
    [InlineData("")]
    public void Other_unknown_errors_keep_the_generic_text(string raw)
    {
        Assert.Equal("Something went wrong. Try again.", ErrorText.For(ErrorMapper.Map(new Exception(raw)), raw));
    }
}
