using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DotVVM.Framework.Hosting;
using DotVVM.Framework.ViewModel;

namespace DotVVM.Samples.ApplicationInsights.Owin.ViewModels.Test
{
    public class CorrectCommandViewModel : DotvvmViewModelBase
    {
        public void Submit()
        {
        }

        public void Redirect()
        {
            Context.RedirectToRoute("default");
        }
    }
}
