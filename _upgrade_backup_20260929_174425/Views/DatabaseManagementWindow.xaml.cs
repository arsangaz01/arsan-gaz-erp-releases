using System;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Views;

public partial class DatabaseManagementWindow : Window
{
	public DatabaseManagementWindow()
	{
		InitializeComponent();
		Loaded += async (_, _) => await LoadAsync();
	}

	private async Task LoadAsync()
	{
		try
		{
			await using ArsanGazDbContext db = new();
			CustomersGrid.ItemsSource = await db.Customers.AsNoTracking().ToListAsync();
			ProductsGrid.ItemsSource = await db.Products.AsNoTracking().ToListAsync();
			CylindersGrid.ItemsSource = await db.Cylinders.AsNoTracking().ToListAsync();
			QuotesGrid.ItemsSource = await db.Quotes.AsNoTracking().ToListAsync();
			InvoicesGrid.ItemsSource = await db.Invoices.AsNoTracking().ToListAsync();
			TasksGrid.ItemsSource = await db.Tasks.AsNoTracking().ToListAsync();

			int customerCount = await db.Customers.CountAsync();
			int productCount = await db.Products.CountAsync();
			int cylinderCount = await db.Cylinders.CountAsync();
			int quoteCount = await db.Quotes.CountAsync();
			int invoiceCount = await db.Invoices.CountAsync();
			int taskCount = await db.Tasks.CountAsync();

			Summary.Text = $"Müşteri {customerCount:N0}   ·   Ürün {productCount:N0}   ·   Tüp {cylinderCount:N0}   ·   Teklif {quoteCount:N0}   ·   Fatura {invoiceCount:N0}   ·   Görev {taskCount:N0}";
			UpdatedAt.Text = $"Son yenileme {DateTime.Now:HH:mm}";
		}
		catch (Exception exception)
		{
			Summary.Text = "Veriler yüklenemedi: " + exception.GetBaseException().Message;
			UpdatedAt.Text = string.Empty;
		}
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}