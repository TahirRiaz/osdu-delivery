
CREATE VIEW [arc].[V_Mobilapp_Fact_salg_merged]
AS
SELECT
	[MobilAppSalgPK] AS [MobilAppSalgPK]
    ,[Betalingsstatus] AS [Betalingsstatus]
	,[Billettype_navn] AS [Billettype_navn]
    ,[Bruker_ID] AS [Bruker_ID]

    ,[Fra_Sone_ID] AS [Fra_Sone_ID]
    ,[Fra_sone_navn] AS [Fra_sone_navn]
    ,[Fra_stopp_ID] AS [Fra_stopp_ID]
    ,[Fra_stopp_Navn] AS [Fra_stopp_Navn]

    ,[Til_sone_ID] AS [Til_sone_ID]
    ,[Til_sone_navn] AS [Til_sone_navn]
    ,[Til_stopp_ID] AS [Til_stopp_ID]
    ,[Til_stopp_navn] AS [Til_stopp_navn]

    ,[Antall_Soner]
	,NULL AS [allZones]

    ,[Beløp]
    ,[vatamount]
	, NULL as [vatPercentage]
	,[Belop_eks_mva]

	,[Antall]
	
	,[Koblet_til_bedrift_ID]
	,[koblet_til_bedrift_navn]
    ,[Antall_inspeksjoner]
    ,[Ant_HJH_Billett_Ord_DW]
    ,[Ant_HJH_Billett_Kamp_DW]
    ,[Ant_Billett_knyttet_til_Bedrift_DW]
    ,[Ant_30dager_ovrige_kunder_DW]
    ,[Ant_EnkeltBilletter_knyttet_til_Bedrift_DW]
    ,[Ant_HJH_FoersteKjoep_DW]
    ,[Ant_HJH_Gjenkjoep_DW]

    ,[PeriodID]
    ,[SourceSystemID]
    ,[Billett_ID]
    ,[Billett_element_ID]
    ,[Billettype_ID]

	,[Billettkanal_ID]
    ,[Billettkanal_navn]
    ,[Betalingskanal_ID]
    ,[Betalingskanal_navn]
    ,[Foreldrebruker_ID]
    ,[Betalt_av_bedrift_ID]

    ,[Kjopstidspunkt]
    ,[Salgsdato]
    
    ,[Rabattprosent]
    ,[Rabatt]
    
    ,[Enhetstype]
    ,[Refundert]
    ,[Refundert_belop]

	,NULL AS [creditAmount]
    ,NULL AS [creditDate]

	,NULL AS [csComment]
    ,NULL AS [csInvoiceReference]
    ,NULL AS [csOrderedBy]

	,NULL AS [appInstanceName]
    ,NULL AS [id]
    ,CAST(Salgsdato AS datetime) + CAST(Kjopstidspunkt AS datetime) AS [orderDate]
    ,NULL AS [orderId]
    ,NULL AS [orderStatusDate]
    ,NULL AS [owner]
    ,NULL AS [passenger_amount]
    ,NULL AS [passenger_id]
    ,NULL AS [distributionType]

    ,NULL AS [passenger_profileId]
    ,NULL AS [passenger_vatAmount]
    ,NULL AS [passenger_vatPercentage]

	,NULL AS [payerAppInstanceName]
    ,NULL AS [payerAppPlatform]
    ,NULL AS [payerAppVersion]
    ,NULL AS [payerId]
    ,NULL AS [payerOsVersion]
    ,NULL AS [payerTelephoneType]

	,NULL AS [paymentId]
    ,NULL AS [paymentMethod]
    ,NULL AS [paymentStatus]
    ,NULL AS [productTemplateId]
    ,NULL AS [ticketNumber]
    ,NULL AS [ticketStatus]
    ,NULL AS [ticketType]
    ,NULL AS [transType]

	,NULL AS [validFrom]
    ,NULL AS [validTo]

FROM
    [arc].[V_Mobilapp_Fact_salg]
UNION ALL
SELECT
	NULL AS [MobilAppSalgPK]
    ,[orderStatus] AS [Betalingsstatus]
	,[passenger_profile] AS [Billettype_navn]
	,[appInstanceId] AS [Bruker_ID]

	,NULL AS [Fra_Sone_ID]
    ,[fromZone] AS [Fra_sone_navn]
    ,NULL AS [Fra_stopp_ID]
    ,[fromStop] AS [Fra_stopp_Navn]

    , NULL AS [Til_sone_ID]
    ,[toZone] AS [Til_sone_navn]
	,NULL AS [Til_stopp_ID]
    ,[toStop] AS Til_stopp_navn

    ,[nrOfZones] AS [Antall_Soner]
	,[allZones] AS [allZones]
    
    ,CAST([amount] AS numeric(10, 0)) AS [Beløp]
	,[vatAmount] AS [vatamount]
	,[vatPercentage]
	, NULL AS [Belop_eks_mva]

	,[passenger_count] AS [Antall]     

    ,[companyAgreementRef] AS [Koblet_til_bedrift_ID]
	, NULL AS [koblet_til_bedrift_navn]
    , NULL AS [Antall_inspeksjoner]
    , NULL AS [Ant_HJH_Billett_Ord_DW]
    , NULL AS [Ant_HJH_Billett_Kamp_DW]
    , NULL AS [Ant_Billett_knyttet_til_Bedrift_DW]
    , NULL AS [Ant_30dager_ovrige_kunder_DW]
    , NULL AS [Ant_EnkeltBilletter_knyttet_til_Bedrift_DW]
    , NULL AS [Ant_HJH_FoersteKjoep_DW]
    , NULL AS [Ant_HJH_Gjenkjoep_DW]

	, NULL AS [PeriodID]
    --, NULL AS [BillettypeID]
    , NULL AS [SourceSystemID]
    , NULL AS [Billett_ID]
    , NULL AS [Billett_element_ID]
	,[passenger_productId] AS [Billettype_ID]

	, NULL AS [Billettkanal_ID]
    , NULL AS [Billettkanal_navn]
    , NULL AS [Betalingskanal_ID]
    , NULL AS [Betalingskanal_navn]
    , NULL AS [Foreldrebruker_ID]
    , NULL AS [Betalt_av_bedrift_ID]

	,NULL AS [Kjopstidspunkt]
    ,NULL AS [Salgsdato]

	, NULL AS [Rabattprosent]
    , NULL AS [Rabatt]

	, NULL AS [Enhetstype]
    , NULL AS [Refundert]
    , NULL AS [Refundert_belop]

	,[creditAmount]
    ,[creditDate]

    ,[csComment]
    ,[csInvoiceReference]
    ,[csOrderedBy]

     ,[appInstanceName]
     ,[id]
     ,[orderDate]
     ,[orderId]
     ,[orderStatusDate]
     ,[owner]
     ,[passenger_amount]
     ,[passenger_id]
     ,[distributionType]
    
    ,[passenger_profileId]
    ,[passenger_vatAmount]
    ,[passenger_vatPercentage]

    ,[payerAppInstanceName]
    ,[payerAppPlatform]
    ,[payerAppVersion]
    ,[payerId]
    ,[payerOsVersion]
    ,[payerTelephoneType]

    ,[paymentId]
    ,[paymentMethod]
    ,[paymentStatus]
    ,[productTemplateId]
    ,[ticketNumber]
    ,[ticketStatus]
    ,[ticketType]
    ,[transType]

    ,[validFrom]
    ,[validTo]
FROM
    [arc].[Billettapp_Trans]

