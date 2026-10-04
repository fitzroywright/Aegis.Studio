namespace Aegis.Studio.WorkDay;

public static class WorkDayTheme
{
    public static ResourceDictionary Create()
    {
        var resources=new ResourceDictionary();
        resources.Add(new Style(typeof(ContentPage)){Setters={new Setter{Property=VisualElement.BackgroundColorProperty,Value=Color.FromArgb("#FAF9F6")}}});
        resources.Add(new Style(typeof(Label)){Setters={new Setter{Property=Label.TextColorProperty,Value=Color.FromArgb("#30291F")}}});
        resources.Add(new Style(typeof(Button)){Setters={new Setter{Property=Button.BackgroundColorProperty,Value=Color.FromArgb("#574326")},new Setter{Property=Button.TextColorProperty,Value=Colors.White},new Setter{Property=Button.CornerRadiusProperty,Value=8},new Setter{Property=Button.FontSizeProperty,Value=14}}});
        resources.Add(new Style(typeof(Entry)){Setters={new Setter{Property=Entry.TextColorProperty,Value=Color.FromArgb("#30291F")},new Setter{Property=Entry.PlaceholderColorProperty,Value=Color.FromArgb("#7C715F")}}});
        resources.Add(new Style(typeof(Editor)){Setters={new Setter{Property=Editor.TextColorProperty,Value=Color.FromArgb("#30291F")}}});
        return resources;
    }
}
