using DotVVM.Framework.ViewModel;
using System;
using System.ComponentModel.DataAnnotations;

namespace DotVVM.Samples.BasicSamples.ViewModels.FeatureSamples.Validation
{
    public class DateTimeValidationViewModel : DotvvmViewModelBase
    {
        // This viewmodel tests even Validation.Target on each property.

        [Required]
        [DotvvmClientFormat(Disable = true)]
        public DateTime DateTimeTestValue { get; set; }

        public bool DateTimeTestResult { get; set; }
        
        private DateTime defaultValue = new DateTime(2016, 3, 1);

        public void ValidateRequiredDateTime()
        {
            DateTimeTestResult = true;
        }

    }
}
